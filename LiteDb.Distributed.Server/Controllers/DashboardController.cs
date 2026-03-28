using System.Net.Http.Json;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("dashboard/api")]
public sealed class DashboardController : ControllerBase
{
    private readonly ClusterNodeOptions _nodeOptions;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
    private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        ClusterNodeOptions nodeOptions,
        ILogicalDatabaseCatalog logicalDatabaseCatalog,
        ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<DashboardController> logger)
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

        var peerBaseUrlsByNode = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seedPeer in _nodeOptions.SeedPeers)
        {
            RegisterPeer(peerBaseUrlsByNode, seedPeer);
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
                var store = await _logicalDatabaseStoreProvider
                    .GetStoreAsync(registration.DatabaseName, registration.Credential, cancellationToken)
                    .ConfigureAwait(false);

                var peers = await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);
                var businessCollections = await store.GetBusinessCollectionNamesAsync(cancellationToken).ConfigureAwait(false);
                var metadataCollections = await store.GetMetadataCollectionNamesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var peer in peers)
                {
                    RegisterPeer(peerBaseUrlsByNode, peer);
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
                _logger.LogWarning(
                    ex,
                    "Dashboard could not inspect logical database. Database={Database} NodeId={NodeId}",
                    registration.DatabaseName,
                    _nodeOptions.NodeId);

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
        var nodeStatuses = new List<DashboardNodeStatusDto>
        {
            new()
            {
                NodeId = _nodeOptions.NodeId,
                BaseUrl = localBaseUrl,
                IsOnline = true,
                Status = "Online",
                Error = null,
                LastCheckedUtc = now
            }
        };

        var peerProbeTasks = peerBaseUrlsByNode
            .Where(x => !string.Equals(x.Key, _nodeOptions.NodeId, StringComparison.Ordinal))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(peer => ProbePeerAsync(peer.Key, peer.Value, cancellationToken))
            .ToList();

        var peerStatuses = await Task.WhenAll(peerProbeTasks).ConfigureAwait(false);
        nodeStatuses.AddRange(peerStatuses);

        return Ok(new DashboardOverviewDto
        {
            NodeId = _nodeOptions.NodeId,
            TimestampUtc = now,
            DataRootPath = dataRootPath,
            NodeDataPath = nodeDataPath,
            Nodes = nodeStatuses,
            Databases = databaseStatuses
        });
    }

    private async Task<DashboardNodeStatusDto> ProbePeerAsync(
        string peerNodeId,
        string peerBaseUrl,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTime.UtcNow;
        var normalizedBaseUrl = NormalizeBaseUrl(peerBaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            return new DashboardNodeStatusDto
            {
                NodeId = peerNodeId,
                BaseUrl = peerBaseUrl,
                IsOnline = false,
                Status = "Offline",
                Error = "No base URL configured for peer.",
                LastCheckedUtc = checkedAt
            };
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{normalizedBaseUrl}/node", UriKind.Absolute));
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new DashboardNodeStatusDto
                {
                    NodeId = peerNodeId,
                    BaseUrl = normalizedBaseUrl,
                    IsOnline = false,
                    Status = "Offline",
                    Error = $"HTTP {(int)response.StatusCode}",
                    LastCheckedUtc = checkedAt
                };
            }

            var nodeInfo = await response.Content
                .ReadFromJsonAsync<NodeInfoResponse>(cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);

            return new DashboardNodeStatusDto
            {
                NodeId = string.IsNullOrWhiteSpace(nodeInfo?.NodeId) ? peerNodeId : nodeInfo.NodeId,
                BaseUrl = normalizedBaseUrl,
                IsOnline = true,
                Status = "Online",
                Error = null,
                LastCheckedUtc = checkedAt
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DashboardNodeStatusDto
            {
                NodeId = peerNodeId,
                BaseUrl = normalizedBaseUrl,
                IsOnline = false,
                Status = "Offline",
                Error = "Timeout",
                LastCheckedUtc = checkedAt
            };
        }
        catch (Exception ex)
        {
            return new DashboardNodeStatusDto
            {
                NodeId = peerNodeId,
                BaseUrl = normalizedBaseUrl,
                IsOnline = false,
                Status = "Offline",
                Error = ex.Message,
                LastCheckedUtc = checkedAt
            };
        }
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

    private static void RegisterPeer(IDictionary<string, string> peersByNodeId, ClusterPeer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.NodeId))
        {
            return;
        }

        RegisterPeer(peersByNodeId, peer.NodeId, peer.BaseUrl);
    }

    private static void RegisterPeer(IDictionary<string, string> peersByNodeId, string nodeId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (!peersByNodeId.TryGetValue(nodeId, out var existing))
        {
            peersByNodeId[nodeId] = normalizedBaseUrl;
            return;
        }

        if (string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            peersByNodeId[nodeId] = normalizedBaseUrl;
        }
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

    public sealed class DashboardOverviewDto
    {
        public required string NodeId { get; init; }
        public required DateTime TimestampUtc { get; init; }
        public required string DataRootPath { get; init; }
        public required string NodeDataPath { get; init; }
        public IReadOnlyList<DashboardNodeStatusDto> Nodes { get; init; } = Array.Empty<DashboardNodeStatusDto>();
        public IReadOnlyList<DashboardDatabaseStatusDto> Databases { get; init; } = Array.Empty<DashboardDatabaseStatusDto>();
    }

    public sealed class DashboardNodeStatusDto
    {
        public required string NodeId { get; init; }
        public required string BaseUrl { get; init; }
        public required bool IsOnline { get; init; }
        public required string Status { get; init; }
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
