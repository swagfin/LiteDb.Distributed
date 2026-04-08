using System.Buffers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("dashboard/api")]
    public class DashboardController : ControllerBase
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider, IHttpClientFactory httpClientFactory, ILogger<DashboardController> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _logicalDatabaseStoreProvider = logicalDatabaseStoreProvider ?? throw new ArgumentNullException(nameof(logicalDatabaseStoreProvider));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("overview")]
        public async Task<IActionResult> GetOverviewAsync(CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            string dataRootPath = ResolveDataDirectory(_nodeOptions.DataDirectory);
            string nodeDataPath = Path.Combine(dataRootPath, _nodeOptions.NodeId);
            IReadOnlyList<LogicalDatabaseRegistration> registrations = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, DashboardPeerTarget> peerTargetsByNode = new Dictionary<string, DashboardPeerTarget>(StringComparer.Ordinal);

            foreach (ClusterPeer seedPeer in _nodeOptions.SeedPeers)
            {
                RegisterPeer(peerTargetsByNode, seedPeer);
            }

            List<DashboardDatabaseStatusDto> databaseStatuses = new List<DashboardDatabaseStatusDto>(registrations.Count);

            foreach (LogicalDatabaseRegistration? registration in registrations.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
            {
                string businessPath = Path.Combine(nodeDataPath, $"{registration.DatabaseName}.db");
                string metadataPath = Path.Combine(nodeDataPath, $"{registration.DatabaseName}.db.metadata");
                DashboardFileStatusDto businessFile = BuildFileStatus(businessPath);
                DashboardFileStatusDto metadataFile = BuildFileStatus(metadataPath);

                try
                {
                    LiteDbNodeStore store = await _logicalDatabaseStoreProvider.GetStoreAsync(registration.DatabaseName, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<ClusterPeer> peers = await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<string> businessCollections = await store.GetBusinessCollectionNamesAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<string> metadataCollections = await store.GetMetadataCollectionNamesAsync(cancellationToken).ConfigureAwait(false);

                    foreach (ClusterPeer peer in peers)
                    {
                        RegisterPeer(peerTargetsByNode, peer);
                    }

                    databaseStatuses.Add(new DashboardDatabaseStatusDto
                    {
                        Name = registration.DatabaseName,
                        Status = "Healthy",
                        Error = null,
                        BusinessFile = businessFile,
                        MetadataFile = metadataFile,
                        PeerCount = peers.Count,
                        BusinessCollections = businessCollections,
                        MetadataCollections = metadataCollections
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dashboard could not inspect logical database. Database={Database} NodeId={NodeId}", registration.DatabaseName, _nodeOptions.NodeId);

                    databaseStatuses.Add(new DashboardDatabaseStatusDto
                    {
                        Name = registration.DatabaseName,
                        Status = "Error",
                        Error = ex.Message,
                        BusinessFile = businessFile,
                        MetadataFile = metadataFile,
                        PeerCount = 0,
                        BusinessCollections = Array.Empty<string>(),
                        MetadataCollections = Array.Empty<string>()
                    });
                }
            }

            string localBaseUrl = $"{Request.Scheme}://{Request.Host}";
            DashboardNodeStatusDto localStatus = new DashboardNodeStatusDto
            {
                NodeId = _nodeOptions.NodeId,
                BaseUrl = localBaseUrl,
                IsOnline = true,
                Status = "Online",
                HttpStatus = "Online",
                WebSocketStatus = "Local",
                HttpProbeDurationMs = 0,
                WebSocketProbeDurationMs = 0,
                Error = null,
                LastCheckedUtc = now
            };

            List<Task<DashboardPeerProbeResult>> peerProbeTasks = peerTargetsByNode.Values.Where(x => !string.Equals(x.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal)).OrderBy(x => x.NodeId, StringComparer.Ordinal).Select(peer => ProbePeerAsync(peer, cancellationToken)).ToList();

            DashboardPeerProbeResult[] peerProbeResults = await Task.WhenAll(peerProbeTasks).ConfigureAwait(false);
            List<DashboardNodeStatusDto> nodeStatuses = new List<DashboardNodeStatusDto>(1 + peerProbeResults.Length)
            {
                localStatus
            };
            nodeStatuses.AddRange(peerProbeResults.Select(x => x.NodeStatus));

            List<DashboardPeerConnectivityDto> peerConnections = peerProbeResults.Select(x => x.PeerConnectivity).OrderBy(x => x.PeerNodeId, StringComparer.Ordinal).ToList();

            return Ok(new DashboardOverviewDto
            {
                NodeId = _nodeOptions.NodeId,
                TimestampUtc = now,
                DataRootPath = dataRootPath,
                NodeDataPath = nodeDataPath,
                Nodes = nodeStatuses,
                Databases = databaseStatuses,
                PeerConnections = peerConnections
            });
        }

        private async Task<DashboardPeerProbeResult> ProbePeerAsync(DashboardPeerTarget target, CancellationToken cancellationToken)
        {
            DateTime checkedAt = DateTime.UtcNow;
            string normalizedBaseUrl = NormalizeBaseUrl(target.BaseUrl);
            if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            {
                string error = "No base URL configured for peer.";

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
                        LastCheckedUtc = checkedAt
                    });
            }

            HttpProbeResult httpProbe = await ProbeHttpAsync(target.NodeId, normalizedBaseUrl, cancellationToken).ConfigureAwait(false);
            WebSocketProbeResult wsProbe = httpProbe.IsOnline ? await ProbeWebSocketAsync(normalizedBaseUrl, cancellationToken).ConfigureAwait(false) : WebSocketProbeResult.Skipped("HTTP probe failed");

            string overall = DetermineOverallStatus(httpProbe.IsOnline, wsProbe.IsOnline);
            string nodeStatus = MapNodeStatus(overall);
            string? combinedError = CombineErrors(httpProbe.Error, wsProbe.Error);

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
                    LastCheckedUtc = checkedAt
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

        private static DashboardFileStatusDto BuildFileStatus(string path)
        {
            bool exists = System.IO.File.Exists(path);
            if (!exists)
            {
                return new DashboardFileStatusDto
                {
                    Path = path,
                    Exists = false,
                    SizeBytes = 0,
                    LastWriteUtc = null
                };
            }

            FileInfo info = new FileInfo(path);
            return new DashboardFileStatusDto
            {
                Path = info.FullName,
                Exists = true,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc
            };
        }

        private static void RegisterPeer(IDictionary<string, DashboardPeerTarget> peersByNodeId, ClusterPeer peer)
        {
            if (string.IsNullOrWhiteSpace(peer.NodeId))
            {
                return;
            }

            RegisterPeer(peersByNodeId, peer.NodeId, peer.BaseUrl, peer.IsActive);
        }

        private static void RegisterPeer(IDictionary<string, DashboardPeerTarget> peersByNodeId, string nodeId, string baseUrl, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return;
            }

            string normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            if (!peersByNodeId.TryGetValue(nodeId, out DashboardPeerTarget? existing))
            {
                peersByNodeId[nodeId] = new DashboardPeerTarget(nodeId, normalizedBaseUrl, isActive);
                return;
            }

            string mergedBaseUrl = string.IsNullOrWhiteSpace(existing.BaseUrl) ? normalizedBaseUrl : existing.BaseUrl;
            bool mergedActive = existing.IsActive || isActive;
            peersByNodeId[nodeId] = new DashboardPeerTarget(nodeId, mergedBaseUrl, mergedActive);
        }

        private static string ResolveDataDirectory(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            }

            return Path.IsPathRooted(dataDirectory)
                ? Path.GetFullPath(dataDirectory)
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dataDirectory));
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
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

        private class DashboardPeerTarget
        {
            public DashboardPeerTarget(string nodeId, string baseUrl, bool isActive)
            {
                NodeId = nodeId;
                BaseUrl = baseUrl;
                IsActive = isActive;
            }

            public string NodeId { get; set; }
            public string BaseUrl { get; set; }
            public bool IsActive { get; set; }
        }

        private class DashboardPeerProbeResult
        {
            public DashboardPeerProbeResult(DashboardNodeStatusDto nodeStatus, DashboardPeerConnectivityDto peerConnectivity)
            {
                NodeStatus = nodeStatus;
                PeerConnectivity = peerConnectivity;
            }

            public DashboardNodeStatusDto NodeStatus { get; set; }
            public DashboardPeerConnectivityDto PeerConnectivity { get; set; }
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

            public static WebSocketProbeResult Skipped(string reason) => new(false, "Skipped", reason, null);
        }

        public class DashboardOverviewDto
        {
            public required string NodeId { get; set; }
            public required DateTime TimestampUtc { get; set; }
            public required string DataRootPath { get; set; }
            public required string NodeDataPath { get; set; }
            public IReadOnlyList<DashboardNodeStatusDto> Nodes { get; set; } = Array.Empty<DashboardNodeStatusDto>();
            public IReadOnlyList<DashboardPeerConnectivityDto> PeerConnections { get; set; } = Array.Empty<DashboardPeerConnectivityDto>();
            public IReadOnlyList<DashboardDatabaseStatusDto> Databases { get; set; } = Array.Empty<DashboardDatabaseStatusDto>();
        }

        public class DashboardNodeStatusDto
        {
            public required string NodeId { get; set; }
            public required string BaseUrl { get; set; }
            public required bool IsOnline { get; set; }
            public required string Status { get; set; }
            public required string HttpStatus { get; set; }
            public required string WebSocketStatus { get; set; }
            public required double? HttpProbeDurationMs { get; set; }
            public required double? WebSocketProbeDurationMs { get; set; }
            public required string? Error { get; set; }
            public required DateTime LastCheckedUtc { get; set; }
        }

        public class DashboardPeerConnectivityDto
        {
            public required string PeerNodeId { get; set; }
            public required string BaseUrl { get; set; }
            public required bool IsPeerActive { get; set; }
            public required string OverallStatus { get; set; }
            public required string HttpStatus { get; set; }
            public required string WebSocketStatus { get; set; }
            public required double? HttpProbeDurationMs { get; set; }
            public required double? WebSocketProbeDurationMs { get; set; }
            public required string? Error { get; set; }
            public required DateTime LastCheckedUtc { get; set; }
        }

        public class DashboardDatabaseStatusDto
        {
            public required string Name { get; set; }
            public required string Status { get; set; }
            public required string? Error { get; set; }
            public required DashboardFileStatusDto BusinessFile { get; set; }
            public required DashboardFileStatusDto MetadataFile { get; set; }
            public required int PeerCount { get; set; }
            public IReadOnlyList<string> BusinessCollections { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> MetadataCollections { get; set; } = Array.Empty<string>();
        }

        public class DashboardFileStatusDto
        {
            public required string Path { get; set; }
            public required bool Exists { get; set; }
            public required long SizeBytes { get; set; }
            public required DateTime? LastWriteUtc { get; set; }
        }
    }

}
