using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiteDb.Distributed.Server.Domain.Abstractions;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Context;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Server.Replication
{
    public class PeerReplicationSignalService : BackgroundService, IReplicationSignalPublisher, IReplicationWebSocketHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(30);
        private const int MaxWebSocketTextMessageBytes = 16 * 1024;

        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IDatabaseContextAccessor _databaseContextAccessor;
        private readonly IClusterPeerRegistry _clusterPeerRegistry;
        private readonly IReplicationOrchestrator _replicationOrchestrator;
        private readonly ILogger<PeerReplicationSignalService> _logger;
        private readonly TimeSpan _webSocketAckTimeout;
        private readonly ConcurrentDictionary<string, ScheduledDispatch> _scheduledDispatches = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _dispatchSignal = new(0, int.MaxValue);

        public PeerReplicationSignalService(ClusterNodeOptions nodeOptions, IDatabaseContextAccessor databaseContextAccessor, IClusterPeerRegistry clusterPeerRegistry, IReplicationOrchestrator replicationOrchestrator, ILogger<PeerReplicationSignalService> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
            _clusterPeerRegistry = clusterPeerRegistry ?? throw new ArgumentNullException(nameof(clusterPeerRegistry));
            _replicationOrchestrator = replicationOrchestrator ?? throw new ArgumentNullException(nameof(replicationOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            int ackTimeoutMs = Math.Clamp(_nodeOptions.ReplicationSignalAckTimeoutMilliseconds, 1000, 120000);
            _webSocketAckTimeout = TimeSpan.FromMilliseconds(ackTimeoutMs);
        }

        public void NotifyLocalChange(string reason)
        {
            DatabaseRequestContext? context = _databaseContextAccessor.Current;
            if (context is null)
            {
                _logger.LogDebug("Replication signal enqueue skipped because no active database context. Reason={Reason}", reason);
                return;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "local-change";
            }

            DateTime now = DateTime.UtcNow;
            ScheduledDispatch scheduled = _scheduledDispatches.AddOrUpdate(
                context.DatabaseName,
                _ => new ScheduledDispatch(context.DatabaseName, reason, 0, now, now),
                (_, existing) => new ScheduledDispatch(existing.DatabaseName, reason, 0, now, now));

            _dispatchSignal.Release();
            _logger.LogDebug("Replication dispatch scheduled. Database={Database} Reason={Reason} DueUtc={DueUtc}", scheduled.DatabaseName, scheduled.Reason, scheduled.DueUtc);
        }

        public async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(webSocket);
            _logger.LogDebug("Replication websocket connection accepted. LocalNodeId={LocalNodeId}", _nodeOptions.NodeId);

            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? payload;
                try
                {
                    payload = await ReceiveTextMessageAsync(webSocket, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Replication websocket connection closed due to invalid message size. LocalNodeId={LocalNodeId}", _nodeOptions.NodeId);
                    await TryCloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "message-too-big", cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (payload is null)
                {
                    break;
                }

                ReplicationSignalMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<ReplicationSignalMessage>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Replication websocket payload rejected due to invalid JSON. LocalNodeId={LocalNodeId}", _nodeOptions.NodeId);
                    await TrySendAckAsync(webSocket, accepted: false, error: "invalid-json", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!IsValidSyncRequest(message))
                {
                    if (IsHealthCheck(message))
                    {
                        _logger.LogDebug("Replication websocket health-check received. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId}", _nodeOptions.NodeId, message!.SourceNodeId);
                        await TrySendAckAsync(webSocket, accepted: true, error: null, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning("Replication websocket payload rejected due to invalid content. LocalNodeId={LocalNodeId}", _nodeOptions.NodeId);
                    await TrySendAckAsync(webSocket, accepted: false, error: "invalid-payload", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                System.Diagnostics.Stopwatch applyStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogDebug("Replication websocket signal received. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} Database={Database} Reason={Reason}", _nodeOptions.NodeId, message!.SourceNodeId, message.Database, message.Reason);

                try
                {
                    // Acknowledge immediately so sender does not block on receiver-side replication work.
                    await TrySendAckAsync(webSocket, accepted: true, error: null, cancellationToken).ConfigureAwait(false);
                    await _replicationOrchestrator.ReplicateDatabaseAsync(message.Database, _nodeOptions.ReplicationApiKey, $"websocket:{message.SourceNodeId}", cancellationToken).ConfigureAwait(false);
                    applyStopwatch.Stop();

                    _logger.LogDebug("Replication websocket signal applied. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} Database={Database} DurationMs={DurationMs}", _nodeOptions.NodeId, message.SourceNodeId, message.Database, applyStopwatch.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    applyStopwatch.Stop();
                    _logger.LogWarning(ex, "Replication websocket signal failed. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} Database={Database} DurationMs={DurationMs}", _nodeOptions.NodeId, message.SourceNodeId, message.Database, applyStopwatch.Elapsed.TotalMilliseconds);
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Peer replication signal worker started. LocalNodeId={LocalNodeId}", _nodeOptions.NodeId);

            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForDueDispatchWindowAsync(stoppingToken).ConfigureAwait(false);

                DateTime now = DateTime.UtcNow;
                List<string> dueDispatches = _scheduledDispatches.Where(x => x.Value.DueUtc <= now).Select(x => x.Key).ToList();

                foreach (string? key in dueDispatches)
                {
                    if (!_scheduledDispatches.TryRemove(key, out ScheduledDispatch? dispatch))
                    {
                        continue;
                    }

                    await ProcessDispatchAsync(dispatch, stoppingToken).ConfigureAwait(false);
                }
            }
        }

        private async Task WaitForDueDispatchWindowAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_scheduledDispatches.IsEmpty)
                {
                    await _dispatchSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    DrainDispatchSignal();
                    return;
                }

                DateTime now = DateTime.UtcNow;
                DateTime nextDueUtc = _scheduledDispatches.Values.Min(x => x.DueUtc);
                if (nextDueUtc <= now)
                {
                    return;
                }

                TimeSpan wait = nextDueUtc - now;
                bool signaled = await _dispatchSignal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                if (!signaled)
                {
                    return;
                }

                DrainDispatchSignal();
            }
        }

        private async Task ProcessDispatchAsync(ScheduledDispatch dispatch, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch dispatchStopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await _replicationOrchestrator.ReplicateDatabaseAsync(dispatch.DatabaseName, _nodeOptions.ReplicationApiKey, $"local-dispatch:{dispatch.Reason}", cancellationToken).ConfigureAwait(false);
                dispatchStopwatch.Stop();

                _logger.LogDebug("Replication dispatch applied. Database={Database} Attempt={Attempt} DurationMs={DurationMs}", dispatch.DatabaseName, dispatch.Attempt, dispatchStopwatch.Elapsed.TotalMilliseconds);

                await BroadcastSignalToPeersAsync(dispatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                dispatchStopwatch.Stop();

                int nextAttempt = dispatch.Attempt + 1;
                TimeSpan retryDelay = ComputeRetryDelay(nextAttempt);
                DateTime retryDueUtc = DateTime.UtcNow.Add(retryDelay);
                ScheduledDispatch retryDispatch = new ScheduledDispatch(dispatch.DatabaseName, dispatch.Reason, nextAttempt, retryDueUtc, DateTime.UtcNow);

                _scheduledDispatches.AddOrUpdate(dispatch.DatabaseName, _ => retryDispatch, (_, existing) => MergeRetry(existing, retryDispatch));

                _logger.LogWarning(ex, "Replication dispatch failed; retry scheduled. Database={Database} Attempt={Attempt} RetryInMs={RetryInMs} DurationMs={DurationMs}", dispatch.DatabaseName, nextAttempt, retryDelay.TotalMilliseconds, dispatchStopwatch.Elapsed.TotalMilliseconds);
            }
        }

        private async Task BroadcastSignalToPeersAsync(ScheduledDispatch dispatch, CancellationToken cancellationToken)
        {
                using IDisposable scope = _databaseContextAccessor.BeginScope(new DatabaseRequestContext
                {
                    DatabaseName = dispatch.DatabaseName,
                    ApiKey = _nodeOptions.ReplicationApiKey,
                    IsRoot = true,
                CanAddDatabase = true,
                CanDeleteDatabase = true,
                CanReadDocument = true,
                CanWriteDocument = true,
                CanUpdateDocument = true,
                CanDeleteDocument = true
            });

            IReadOnlyList<ClusterPeer> peers = await _clusterPeerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);
            List<ClusterPeer> targets = peers.Where(x => x.IsActive && !string.Equals(x.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal)).ToList();

            if (targets.Count == 0)
            {
                _logger.LogDebug("Replication websocket broadcast skipped because there are no active peers. Database={Database}", dispatch.DatabaseName);
                return;
            }

            ReplicationSignalMessage message = new ReplicationSignalMessage
            {
                Type = "sync-request",
                SourceNodeId = _nodeOptions.NodeId,
                Database = dispatch.DatabaseName,
                ReplicationApiKey = _nodeOptions.ReplicationApiKey,
                Reason = dispatch.Reason,
                TimestampUtc = DateTime.UtcNow
            };

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            int acknowledged = 0;

            foreach (ClusterPeer? peer in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await SendSignalToPeerAsync(peer, dispatch.DatabaseName, payload, cancellationToken).ConfigureAwait(false))
                {
                    acknowledged++;
                }
            }

            _logger.LogDebug("Replication websocket broadcast completed. Database={Database} Peers={Peers} Acked={Acked}", dispatch.DatabaseName, targets.Count, acknowledged);
        }

        private async Task<bool> SendSignalToPeerAsync(ClusterPeer peer, string databaseName, byte[] payload, CancellationToken cancellationToken)
        {
            Uri endpoint = BuildWebSocketEndpoint(peer.BaseUrl);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using ClientWebSocket webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                webSocket.Options.SetRequestHeader("ReplicationApiKey", _nodeOptions.ReplicationApiKey);

                using CancellationTokenSource ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ackTimeout.CancelAfter(_webSocketAckTimeout);

                await webSocket.ConnectAsync(endpoint, ackTimeout.Token).ConfigureAwait(false);
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ackTimeout.Token).ConfigureAwait(false);

                string? ackPayload = await ReceiveTextMessageAsync(webSocket, ackTimeout.Token).ConfigureAwait(false);
                if (ackPayload is null)
                {
                    stopwatch.Stop();
                    _logger.LogWarning("Replication websocket signal was not acknowledged. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Database={Database} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, databaseName, stopwatch.Elapsed.TotalMilliseconds);
                    return false;
                }

                ReplicationSignalAck? ack = JsonSerializer.Deserialize<ReplicationSignalAck>(ackPayload, JsonOptions);
                if (ack is null || !ack.Accepted)
                {
                    stopwatch.Stop();
                    _logger.LogWarning("Replication websocket signal was rejected by peer. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Database={Database} Error={Error} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, databaseName, ack?.Error, stopwatch.Elapsed.TotalMilliseconds);
                    return false;
                }

                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "acknowledged", cancellationToken).ConfigureAwait(false);
                }

                stopwatch.Stop();
                _logger.LogDebug("Replication signal sent. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Database={Database} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, databaseName, stopwatch.Elapsed.TotalMilliseconds);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("Replication signal timed out waiting for ack. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Database={Database} TimeoutMs={TimeoutMs} Endpoint={Endpoint} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, databaseName, _webSocketAckTimeout.TotalMilliseconds, endpoint, stopwatch.Elapsed.TotalMilliseconds);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Replication signal send failed. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Database={Database} Endpoint={Endpoint} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, databaseName, endpoint, stopwatch.Elapsed.TotalMilliseconds);
                return false;
            }
        }

        private static Uri BuildWebSocketEndpoint(string baseUrl)
        {
            Uri baseUri = new Uri(baseUrl, UriKind.Absolute);
            string scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";

            return new UriBuilder(baseUri)
            {
                Scheme = scheme,
                Path = "/ws/replication",
                Query = string.Empty
            }.Uri;
        }

        private bool IsValidSyncRequest(ReplicationSignalMessage? message)
        {
            return message is not null
                   && string.Equals(message.Type, "sync-request", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(message.SourceNodeId)
                   && !string.IsNullOrWhiteSpace(message.Database)
                   && !string.IsNullOrWhiteSpace(message.ReplicationApiKey)
                   && string.Equals(message.ReplicationApiKey, _nodeOptions.ReplicationApiKey, StringComparison.Ordinal)
                   && !string.Equals(message.SourceNodeId, _nodeOptions.NodeId, StringComparison.Ordinal);
        }

        private static bool IsHealthCheck(ReplicationSignalMessage? message)
        {
            return message is not null && string.Equals(message.Type, "health-check", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(8 * 1024);

            try
            {
                using MemoryStream stream = new MemoryStream();

                while (true)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(rented), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State == WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken).ConfigureAwait(false);
                        }

                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    stream.Write(rented, 0, result.Count);

                    if (stream.Length > MaxWebSocketTextMessageBytes)
                    {
                        throw new InvalidOperationException($"Replication websocket text message exceeded {MaxWebSocketTextMessageBytes} bytes.");
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static async Task TrySendAckAsync(WebSocket webSocket, bool accepted, string? error, CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new ReplicationSignalAck { Accepted = accepted, Error = error }, JsonOptions);

            try
            {
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Intentionally ignored: peer may close immediately after sending signal.
            }
        }

        private static async Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string statusDescription, CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open && webSocket.State != WebSocketState.CloseReceived)
            {
                return;
            }

            try
            {
                await webSocket.CloseAsync(status, statusDescription, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort close; the peer may already have gone away.
            }
        }

        private static TimeSpan ComputeRetryDelay(int attempt)
        {
            int boundedAttempt = Math.Clamp(attempt, 1, 10);
            double exponent = Math.Pow(2, boundedAttempt - 1);
            double delayMs = Math.Min(RetryBaseDelay.TotalMilliseconds * exponent, RetryMaxDelay.TotalMilliseconds);
            int jitterMs = Random.Shared.Next(0, 250);
            return TimeSpan.FromMilliseconds(delayMs + jitterMs);
        }

        private void DrainDispatchSignal()
        {
            while (_dispatchSignal.CurrentCount > 0)
            {
                _dispatchSignal.Wait(0);
            }
        }

        private static ScheduledDispatch MergeRetry(ScheduledDispatch existing, ScheduledDispatch retry)
        {
            if (existing.Attempt == 0 && existing.DueUtc <= DateTime.UtcNow)
            {
                return existing;
            }

            DateTime dueUtc = existing.DueUtc <= retry.DueUtc ? existing.DueUtc : retry.DueUtc;
            int attempt = existing.Attempt == 0 ? 0 : Math.Max(existing.Attempt, retry.Attempt);
            string reason = string.IsNullOrWhiteSpace(existing.Reason) ? retry.Reason : existing.Reason;

            return new ScheduledDispatch(existing.DatabaseName, reason, attempt, dueUtc, DateTime.UtcNow);
        }

        private class ScheduledDispatch
        {
            public ScheduledDispatch(string databaseName, string reason, int attempt, DateTime dueUtc, DateTime updatedUtc)
            {
                DatabaseName = databaseName;
                Reason = reason;
                Attempt = attempt;
                DueUtc = dueUtc;
                UpdatedUtc = updatedUtc;
            }

            public string DatabaseName { get; set; }
            public string Reason { get; set; }
            public int Attempt { get; set; }
            public DateTime DueUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private class ReplicationSignalMessage
        {
            public string Type { get; set; } = string.Empty;
            public string SourceNodeId { get; set; } = string.Empty;
            public string Database { get; set; } = string.Empty;
            public string ReplicationApiKey { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public DateTime TimestampUtc { get; set; }
        }

        private class ReplicationSignalAck
        {
            public bool Accepted { get; set; }
            public string? Error { get; set; }
        }
    }

}
