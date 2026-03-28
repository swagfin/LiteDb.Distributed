using System.Buffers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("dashboard/api")]
public sealed class DashboardController : ControllerBase
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
        var now = DateTime.UtcNow;
        var dataRootPath = ResolveDataDirectory(_nodeOptions.DataDirectory);
        var nodeDataPath = Path.Combine(dataRootPath, _nodeOptions.NodeId);
        var registrations = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var peerTargetsByNode = new Dictionary<string, DashboardPeerTarget>(StringComparer.Ordinal);

        foreach (var seedPeer in _nodeOptions.SeedPeers)
        {
            RegisterPeer(peerTargetsByNode, seedPeer);
        }

        var databaseStatuses = new List<DashboardDatabaseStatusDto>(registrations.Count);

        foreach (var registration in registrations.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
        {
            var businessPath = Path.Combine(nodeDataPath, $"{registration.DatabaseName}.db");
            var metadataPath = Path.Combine(nodeDataPath, $"{registration.DatabaseName}.db.metadata");
            var businessFile = BuildFileStatus(businessPath);
            var metadataFile = BuildFileStatus(metadataPath);

            try
            {
                var store = await _logicalDatabaseStoreProvider.GetStoreAsync(registration.DatabaseName, registration.Credential, cancellationToken).ConfigureAwait(false);
                var peers = await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);
                var businessCollections = await store.GetBusinessCollectionNamesAsync(cancellationToken).ConfigureAwait(false);
                var metadataCollections = await store.GetMetadataCollectionNamesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var peer in peers)
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

        var localBaseUrl = $"{Request.Scheme}://{Request.Host}";
        var localStatus = new DashboardNodeStatusDto
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

        var peerProbeTasks = peerTargetsByNode.Values
            .Where(x => !string.Equals(x.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal))
            .OrderBy(x => x.NodeId, StringComparer.Ordinal)
            .Select(peer => ProbePeerAsync(peer, cancellationToken))
            .ToList();

        var peerProbeResults = await Task.WhenAll(peerProbeTasks).ConfigureAwait(false);
        var nodeStatuses = new List<DashboardNodeStatusDto>(1 + peerProbeResults.Length)
        {
            localStatus
        };
        nodeStatuses.AddRange(peerProbeResults.Select(x => x.NodeStatus));

        var peerConnections = peerProbeResults
            .Select(x => x.PeerConnectivity)
            .OrderBy(x => x.PeerNodeId, StringComparer.Ordinal)
            .ToList();

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
        var checkedAt = DateTime.UtcNow;
        var normalizedBaseUrl = NormalizeBaseUrl(target.BaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            var error = "No base URL configured for peer.";

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

        var httpProbe = await ProbeHttpAsync(target.NodeId, normalizedBaseUrl, cancellationToken).ConfigureAwait(false);
        var wsProbe = httpProbe.IsOnline
            ? await ProbeWebSocketAsync(normalizedBaseUrl, cancellationToken).ConfigureAwait(false)
            : WebSocketProbeResult.Skipped("HTTP probe failed");

        var overall = DetermineOverallStatus(httpProbe.IsOnline, wsProbe.IsOnline);
        var nodeStatus = MapNodeStatus(overall);
        var combinedError = CombineErrors(httpProbe.Error, wsProbe.Error);

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{normalizedBaseUrl}/node", UriKind.Absolute));
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new HttpProbeResult(peerNodeId, IsOnline: false, Status: $"HTTP {(int)response.StatusCode}", Error: $"HTTP {(int)response.StatusCode}", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }

            var nodeInfo = await response.Content.ReadFromJsonAsync<NodeInfoResponse>(cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            var resolvedNodeId = string.IsNullOrWhiteSpace(nodeInfo?.NodeId) ? peerNodeId : nodeInfo.NodeId;
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            using var webSocket = new ClientWebSocket();
            var endpoint = BuildWebSocketEndpoint(normalizedBaseUrl);
            await webSocket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);

            var message = JsonSerializer.SerializeToUtf8Bytes(new DashboardWebSocketHealthCheck
            {
                Type = "health-check",
                SourceNodeId = _nodeOptions.NodeId,
                TimestampUtc = DateTime.UtcNow
            }, JsonOptions);

            await webSocket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token).ConfigureAwait(false);

            var responseText = await ReceiveTextMessageAsync(webSocket, timeoutCts.Token).ConfigureAwait(false);
            if (responseText is null)
            {
                return new WebSocketProbeResult(IsOnline: false, Status: "NoAck", Error: "WebSocket closed before ack.", DurationMs: stopwatch.Elapsed.TotalMilliseconds);
            }

            var ack = JsonSerializer.Deserialize<DashboardWebSocketAck>(responseText, JsonOptions);
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
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";

        return new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = "/ws/replication",
            Query = string.Empty
        }.Uri;
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8 * 1024);

        try
        {
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(rented), cancellationToken).ConfigureAwait(false);
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
        var parts = errors
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" | ", parts);
    }

    private static DashboardFileStatusDto BuildFileStatus(string path)
    {
        var exists = System.IO.File.Exists(path);
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

        var info = new FileInfo(path);
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

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (!peersByNodeId.TryGetValue(nodeId, out var existing))
        {
            peersByNodeId[nodeId] = new DashboardPeerTarget(nodeId, normalizedBaseUrl, isActive);
            return;
        }

        var mergedBaseUrl = string.IsNullOrWhiteSpace(existing.BaseUrl) ? normalizedBaseUrl : existing.BaseUrl;
        var mergedActive = existing.IsActive || isActive;
        peersByNodeId[nodeId] = existing with { BaseUrl = mergedBaseUrl, IsActive = mergedActive };
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

    private sealed class NodeInfoResponse
    {
        public string NodeId { get; init; } = string.Empty;
    }

    private sealed class DashboardWebSocketHealthCheck
    {
        public string Type { get; init; } = string.Empty;
        public string SourceNodeId { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; }
    }

    private sealed class DashboardWebSocketAck
    {
        public bool Accepted { get; init; }
        public string? Error { get; init; }
    }

    private sealed record DashboardPeerTarget(string NodeId, string BaseUrl, bool IsActive);
    private sealed record DashboardPeerProbeResult(DashboardNodeStatusDto NodeStatus, DashboardPeerConnectivityDto PeerConnectivity);
    private sealed record HttpProbeResult(string ResolvedNodeId, bool IsOnline, string Status, string? Error, double DurationMs);
    private sealed record WebSocketProbeResult(bool IsOnline, string Status, string? Error, double? DurationMs)
    {
        public static WebSocketProbeResult Skipped(string reason) => new(false, "Skipped", reason, null);
    }

    public sealed class DashboardOverviewDto
    {
        public required string NodeId { get; init; }
        public required DateTime TimestampUtc { get; init; }
        public required string DataRootPath { get; init; }
        public required string NodeDataPath { get; init; }
        public IReadOnlyList<DashboardNodeStatusDto> Nodes { get; init; } = Array.Empty<DashboardNodeStatusDto>();
        public IReadOnlyList<DashboardPeerConnectivityDto> PeerConnections { get; init; } = Array.Empty<DashboardPeerConnectivityDto>();
        public IReadOnlyList<DashboardDatabaseStatusDto> Databases { get; init; } = Array.Empty<DashboardDatabaseStatusDto>();
    }

    public sealed class DashboardNodeStatusDto
    {
        public required string NodeId { get; init; }
        public required string BaseUrl { get; init; }
        public required bool IsOnline { get; init; }
        public required string Status { get; init; }
        public required string HttpStatus { get; init; }
        public required string WebSocketStatus { get; init; }
        public required double? HttpProbeDurationMs { get; init; }
        public required double? WebSocketProbeDurationMs { get; init; }
        public required string? Error { get; init; }
        public required DateTime LastCheckedUtc { get; init; }
    }

    public sealed class DashboardPeerConnectivityDto
    {
        public required string PeerNodeId { get; init; }
        public required string BaseUrl { get; init; }
        public required bool IsPeerActive { get; init; }
        public required string OverallStatus { get; init; }
        public required string HttpStatus { get; init; }
        public required string WebSocketStatus { get; init; }
        public required double? HttpProbeDurationMs { get; init; }
        public required double? WebSocketProbeDurationMs { get; init; }
        public required string? Error { get; init; }
        public required DateTime LastCheckedUtc { get; init; }
    }

    public sealed class DashboardDatabaseStatusDto
    {
        public required string Name { get; init; }
        public required string Status { get; init; }
        public required string? Error { get; init; }
        public required DashboardFileStatusDto BusinessFile { get; init; }
        public required DashboardFileStatusDto MetadataFile { get; init; }
        public required int PeerCount { get; init; }
        public IReadOnlyList<string> BusinessCollections { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MetadataCollections { get; init; } = Array.Empty<string>();
    }

    public sealed class DashboardFileStatusDto
    {
        public required string Path { get; init; }
        public required bool Exists { get; init; }
        public required long SizeBytes { get; init; }
        public required DateTime? LastWriteUtc { get; init; }
    }
}
