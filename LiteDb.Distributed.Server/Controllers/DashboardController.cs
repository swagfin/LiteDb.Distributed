using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Server.Infrastructure.Dashboard;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("dashboard/api")]
    public class DashboardController : ControllerBase
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
        private readonly IReplicationStatusService _replicationStatusService;
        private readonly DashboardPeerProbeService _peerProbeService;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider, IReplicationStatusService replicationStatusService, DashboardPeerProbeService peerProbeService, ILogger<DashboardController> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _logicalDatabaseStoreProvider = logicalDatabaseStoreProvider ?? throw new ArgumentNullException(nameof(logicalDatabaseStoreProvider));
            _replicationStatusService = replicationStatusService ?? throw new ArgumentNullException(nameof(replicationStatusService));
            _peerProbeService = peerProbeService ?? throw new ArgumentNullException(nameof(peerProbeService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("overview")]
        public async Task<IActionResult> GetOverviewAsync(CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            string dataRootPath = DashboardFileHelper.ResolveDataDirectory(_nodeOptions.DataDirectory);
            string nodeDataPath = Path.Combine(dataRootPath, _nodeOptions.NodeId);
            IReadOnlyList<LogicalDatabaseRegistration> registrations = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            ReplicationStatusSnapshot replicationStatus = await _replicationStatusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, ReplicationDatabaseStatus> replicationStatusByDatabase = replicationStatus.Databases.ToDictionary(x => x.DatabaseName, StringComparer.Ordinal);
            Dictionary<string, DashboardPeerTarget> peerTargetsByNode = new Dictionary<string, DashboardPeerTarget>(StringComparer.Ordinal);

            foreach (ClusterPeer seedPeer in _nodeOptions.SeedPeers)
            {
                RegisterPeer(peerTargetsByNode, seedPeer);
            }

            List<DashboardDatabaseStatusDto> databaseStatuses = await BuildDatabaseStatusesAsync(registrations, replicationStatusByDatabase, peerTargetsByNode, nodeDataPath, cancellationToken).ConfigureAwait(false);
            DashboardNodeStatusDto localStatus = BuildLocalNodeStatus(now);
            List<Task<DashboardPeerProbeResult>> peerProbeTasks = peerTargetsByNode.Values
                .Where(x => !string.Equals(x.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal))
                .OrderBy(x => x.NodeId, StringComparer.Ordinal)
                .Select(peer => _peerProbeService.ProbePeerAsync(peer, cancellationToken))
                .ToList();

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

        private async Task<List<DashboardDatabaseStatusDto>> BuildDatabaseStatusesAsync(IReadOnlyList<LogicalDatabaseRegistration> registrations, Dictionary<string, ReplicationDatabaseStatus> replicationStatusByDatabase, Dictionary<string, DashboardPeerTarget> peerTargetsByNode, string nodeDataPath, CancellationToken cancellationToken)
        {
            List<DashboardDatabaseStatusDto> databaseStatuses = new List<DashboardDatabaseStatusDto>(registrations.Count);

            foreach (LogicalDatabaseRegistration registration in registrations.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
            {
                DashboardDatabaseStatusDto status = await BuildDatabaseStatusAsync(registration, replicationStatusByDatabase, peerTargetsByNode, nodeDataPath, cancellationToken).ConfigureAwait(false);
                databaseStatuses.Add(status);
            }

            return databaseStatuses;
        }

        private async Task<DashboardDatabaseStatusDto> BuildDatabaseStatusAsync(LogicalDatabaseRegistration registration, Dictionary<string, ReplicationDatabaseStatus> replicationStatusByDatabase, Dictionary<string, DashboardPeerTarget> peerTargetsByNode, string nodeDataPath, CancellationToken cancellationToken)
        {
            string databasePath = Path.Combine(nodeDataPath, $"{registration.DatabaseName}.db");
            DashboardFileStatusDto databaseFile = DashboardFileHelper.BuildFileStatus(databasePath);
            replicationStatusByDatabase.TryGetValue(registration.DatabaseName, out ReplicationDatabaseStatus? databaseReplicationStatus);

            try
            {
                LiteDbNodeStore store = await _logicalDatabaseStoreProvider.GetStoreAsync(registration.DatabaseName, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ClusterPeer> peers = await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);

                foreach (ClusterPeer peer in peers)
                {
                    RegisterPeer(peerTargetsByNode, peer);
                }

                return BuildDatabaseStatus(registration.DatabaseName, "Healthy", null, databaseFile, peers.Count, databaseReplicationStatus);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dashboard could not inspect logical database. Database={Database} NodeId={NodeId}", registration.DatabaseName, _nodeOptions.NodeId);
                return BuildDatabaseStatus(registration.DatabaseName, "Error", ex.Message, databaseFile, 0, databaseReplicationStatus);
            }
        }

        private DashboardNodeStatusDto BuildLocalNodeStatus(DateTime now)
        {
            return new DashboardNodeStatusDto
            {
                NodeId = _nodeOptions.NodeId,
                BaseUrl = $"{Request.Scheme}://{Request.Host}",
                IsOnline = true,
                Status = "Online",
                HttpStatus = "Online",
                WebSocketStatus = "Local",
                HttpProbeDurationMs = 0,
                WebSocketProbeDurationMs = 0,
                Error = null,
                LastCheckedUtc = now
            };
        }

        private static DashboardDatabaseStatusDto BuildDatabaseStatus(string databaseName, string status, string? error, DashboardFileStatusDto databaseFile, int peerCount, ReplicationDatabaseStatus? replicationStatus)
        {
            return new DashboardDatabaseStatusDto
            {
                Name = databaseName,
                Status = status,
                Error = error,
                DatabaseFile = databaseFile,
                PeerCount = peerCount,
                TotalEstimatedPendingPushOperations = replicationStatus?.TotalEstimatedPendingPushOperations ?? 0,
                ReplicationPeers = BuildDashboardPeerStatuses(replicationStatus)
            };
        }

        private static List<DashboardReplicationPeerStatusDto> BuildDashboardPeerStatuses(ReplicationDatabaseStatus? replicationStatus)
        {
            IReadOnlyList<ReplicationPeerStatus> peers = replicationStatus?.Peers ?? Array.Empty<ReplicationPeerStatus>();
            return peers
                .OrderBy(x => x.PeerNodeId, StringComparer.Ordinal)
                .Select(x => new DashboardReplicationPeerStatusDto
                {
                    PeerNodeId = x.PeerNodeId,
                    CatchUpStatus = x.CatchUpStatus,
                    EstimatedPendingPushOperations = x.EstimatedPendingPushOperations
                })
                .ToList();
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

            string normalizedBaseUrl = DashboardPeerProbeService.NormalizeBaseUrl(baseUrl);
            if (!peersByNodeId.TryGetValue(nodeId, out DashboardPeerTarget? existing))
            {
                peersByNodeId[nodeId] = new DashboardPeerTarget(nodeId, normalizedBaseUrl, isActive);
                return;
            }

            string mergedBaseUrl = string.IsNullOrWhiteSpace(existing.BaseUrl) ? normalizedBaseUrl : existing.BaseUrl;
            bool mergedActive = existing.IsActive || isActive;
            peersByNodeId[nodeId] = new DashboardPeerTarget(nodeId, mergedBaseUrl, mergedActive);
        }
    }
}
