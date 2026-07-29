using LiteDb.Distributed.Server.Configuration;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    public class DashboardPeerProbeService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly DashboardLatencyHistoryStore _latencyHistoryStore;

        public DashboardPeerProbeService(ClusterNodeOptions nodeOptions, IHttpClientFactory httpClientFactory, DashboardLatencyHistoryStore latencyHistoryStore)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _latencyHistoryStore = latencyHistoryStore ?? throw new ArgumentNullException(nameof(latencyHistoryStore));
        }

        public async Task<DashboardPeerProbeResult> ProbePeerAsync(DashboardPeerTarget target, CancellationToken cancellationToken)
        {
            DateTime checkedAt = DateTime.UtcNow;
            string normalizedBaseUrl = NormalizeBaseUrl(target.BaseUrl);
            if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            {
                return BuildMissingPeerResult(target, checkedAt);
            }

            HttpProbeResult httpProbe = await ProbeHttpAsync(target.NodeId, normalizedBaseUrl, cancellationToken).ConfigureAwait(false);
            WebSocketProbeResult wsProbe = httpProbe.IsOnline ? await ProbeWebSocketAsync(normalizedBaseUrl, cancellationToken).ConfigureAwait(false) : WebSocketProbeResult.Skipped("HTTP probe failed");

            string overall = DetermineOverallStatus(httpProbe.IsOnline, wsProbe.IsOnline);
            string nodeStatus = MapNodeStatus(overall);
            string? combinedError = CombineErrors(httpProbe.Error, wsProbe.Error);
            IReadOnlyList<DashboardLatencySampleDto> latencyHistory = _latencyHistoryStore.RecordAndGetHistory(httpProbe.ResolvedNodeId, checkedAt, httpProbe.DurationMs, wsProbe.DurationMs);

            return new DashboardPeerProbeResult(
                new DashboardNodeStatusDto
                {
                    NodeId = httpProbe.ResolvedNodeId,
                    BaseUrl = normalizedBaseUrl,
                    IsOnline = httpProbe.IsOnline,
                    Status = nodeStatus,
                    HttpStatus = httpProbe.Status,
                    WebSocketStatus = wsProbe.Status,
                    HttpProbeDurationMs = httpProbe.DurationMs,
                    WebSocketProbeDurationMs = wsProbe.DurationMs,
                    Error = combinedError,
                    LastCheckedUtc = checkedAt
                },
                new DashboardPeerConnectivityDto
                {
                    PeerNodeId = httpProbe.ResolvedNodeId,
                    BaseUrl = normalizedBaseUrl,
                    IsPeerActive = target.IsActive,
                    OverallStatus = overall,
                    HttpStatus = httpProbe.Status,
                    WebSocketStatus = wsProbe.Status,
                    HttpProbeDurationMs = httpProbe.DurationMs,
                    WebSocketProbeDurationMs = wsProbe.DurationMs,
                    Error = combinedError,
                    LastCheckedUtc = checkedAt,
                    LatencyHistory = latencyHistory
                });
        }

        public static string NormalizeBaseUrl(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        }

        private static DashboardPeerProbeResult BuildMissingPeerResult(DashboardPeerTarget target, DateTime checkedAt)
        {
            string error = "No base URL configured for peer.";
            IReadOnlyList<DashboardLatencySampleDto> emptyHistory = Array.Empty<DashboardLatencySampleDto>();

            return new DashboardPeerProbeResult(
                new DashboardNodeStatusDto
                {
                    NodeId = target.NodeId,
                    BaseUrl = target.BaseUrl,
                    IsOnline = false,
                    Status = "Offline",
                    HttpStatus = "Missing",
                    WebSocketStatus = "Unknown",
                    HttpProbeDurationMs = null,
                    WebSocketProbeDurationMs = null,
                    Error = error,
                    LastCheckedUtc = checkedAt
                },
                new DashboardPeerConnectivityDto
                {
                    PeerNodeId = target.NodeId,
                    BaseUrl = target.BaseUrl,
                    IsPeerActive = target.IsActive,
                    OverallStatus = "Offline",
                    HttpStatus = "Missing",
                    WebSocketStatus = "Unknown",
                    HttpProbeDurationMs = null,
                    WebSocketProbeDurationMs = null,
                    Error = error,
                    LastCheckedUtc = checkedAt,
                    LatencyHistory = emptyHistory
                });
        }

        private async Task<HttpProbeResult> ProbeHttpAsync(string peerNodeId, string normalizedBaseUrl, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                HttpClient client = _httpClientFactory.CreateClient();
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{normalizedBaseUrl}/node", UriKind.Absolute));
                using HttpResponseMessage response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new HttpProbeResult(peerNodeId, IsOnline: false, Status: $"HTTP {(int)response.StatusCode}", Error: $"HTTP {(int)response.StatusCode}", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
                }

                NodeInfoResponse? nodeInfo = await response.Content.ReadFromJsonAsync<NodeInfoResponse>(cancellationToken: timeoutCts.Token).ConfigureAwait(false);
                string resolvedNodeId = string.IsNullOrWhiteSpace(nodeInfo?.NodeId) ? peerNodeId : nodeInfo.NodeId;
                return new HttpProbeResult(resolvedNodeId, IsOnline: true, Status: "Connected", Error: null, DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new HttpProbeResult(peerNodeId, IsOnline: false, Status: "Timeout", Error: "HTTP timeout", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                return new HttpProbeResult(peerNodeId, IsOnline: false, Status: "Error", Error: ex.Message, DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        private async Task<WebSocketProbeResult> ProbeWebSocketAsync(string normalizedBaseUrl, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                using ClientWebSocket webSocket = new ClientWebSocket();
                webSocket.Options.SetRequestHeader("ReplicationApiKey", _nodeOptions.ReplicationApiKey);
                Uri endpoint = BuildWebSocketEndpoint(normalizedBaseUrl);
                await webSocket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);

                byte[] message = JsonSerializer.SerializeToUtf8Bytes(new DashboardWebSocketHealthCheck
                {
                    Type = "health-check",
                    SourceNodeId = _nodeOptions.NodeId,
                    TimestampUtc = DateTime.UtcNow
                }, JsonOptions);

                await webSocket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token).ConfigureAwait(false);

                string? responseText = await ReceiveTextMessageAsync(webSocket, timeoutCts.Token).ConfigureAwait(false);
                if (responseText is null)
                {
                    return new WebSocketProbeResult(IsOnline: false, Status: "NoAck", Error: "WebSocket closed before ack.", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
                }

                DashboardWebSocketAck? ack = JsonSerializer.Deserialize<DashboardWebSocketAck>(responseText, JsonOptions);
                if (ack is null || !ack.Accepted)
                {
                    return new WebSocketProbeResult(IsOnline: false, Status: "Failed", Error: ack?.Error ?? "WebSocket ack rejected.", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
                }

                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "health-check-complete", cancellationToken).ConfigureAwait(false);
                }

                return new WebSocketProbeResult(IsOnline: true, Status: "Okay", Error: null, DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WebSocketProbeResult(IsOnline: false, Status: "Timeout", Error: "WebSocket timeout", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                return new WebSocketProbeResult(IsOnline: false, Status: "Error", Error: ex.Message, DurationMs: stopwatch.Elapsed.TotalMilliseconds);
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
                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    stream.Write(rented, 0, result.Count);
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

        private static string DetermineOverallStatus(bool httpOnline, bool webSocketOnline)
        {
            if (!httpOnline)
            {
                return "Offline";
            }

            return webSocketOnline ? "Connected" : "Degraded";
        }

        private static string MapNodeStatus(string overallStatus)
        {
            return overallStatus switch
            {
                "Connected" => "Online",
                "Degraded" => "Degraded",
                _ => "Offline"
            };
        }

        private static string? CombineErrors(params string?[] errors)
        {
            string[] parts = errors.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            return parts.Length == 0 ? null : string.Join(" | ", parts);
        }

        private class NodeInfoResponse
        {
            public string NodeId { get; set; } = string.Empty;
        }

        private class DashboardWebSocketHealthCheck
        {
            public string Type { get; set; } = string.Empty;
            public string SourceNodeId { get; set; } = string.Empty;
            public DateTime TimestampUtc { get; set; }
        }

        private class DashboardWebSocketAck
        {
            public bool Accepted { get; set; }
            public string? Error { get; set; }
        }

        private class HttpProbeResult
        {
            public HttpProbeResult(string ResolvedNodeId, bool IsOnline, string Status, string? Error, double DurationMs)
            {
                this.ResolvedNodeId = ResolvedNodeId;
                this.IsOnline = IsOnline;
                this.Status = Status;
                this.Error = Error;
                this.DurationMs = DurationMs;
            }

            public string ResolvedNodeId { get; set; }
            public bool IsOnline { get; set; }
            public string Status { get; set; }
            public string? Error { get; set; }
            public double DurationMs { get; set; }
        }

        private class WebSocketProbeResult
        {
            public WebSocketProbeResult(bool IsOnline, string Status, string? Error, double? DurationMs)
            {
                this.IsOnline = IsOnline;
                this.Status = Status;
                this.Error = Error;
                this.DurationMs = DurationMs;
            }

            public bool IsOnline { get; set; }
            public string Status { get; set; }
            public string? Error { get; set; }
            public double? DurationMs { get; set; }

            public static WebSocketProbeResult Skipped(string reason)
            {
                return new WebSocketProbeResult(false, "Skipped", reason, null);
            }
        }
    }
}
