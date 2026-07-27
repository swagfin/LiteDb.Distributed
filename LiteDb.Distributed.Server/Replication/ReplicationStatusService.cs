using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Storage;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Server.Replication
{
    public class ReplicationStatusService : IReplicationStatusService
    {
        private const int MaxLagScanBatchSize = 10_000;
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
        private readonly ILogger<ReplicationStatusService> _logger;

        public ReplicationStatusService(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider, ILogger<ReplicationStatusService> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _logicalDatabaseStoreProvider = logicalDatabaseStoreProvider ?? throw new ArgumentNullException(nameof(logicalDatabaseStoreProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ReplicationStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<LogicalDatabaseRegistration> databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            List<ReplicationDatabaseStatus> databaseStatuses = new List<ReplicationDatabaseStatus>(databases.Count);

            foreach (LogicalDatabaseRegistration database in databases.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
            {
                databaseStatuses.Add(await BuildDatabaseStatusAsync(database.DatabaseName, cancellationToken).ConfigureAwait(false));
            }

            return new ReplicationStatusSnapshot
            {
                NodeId = _nodeOptions.NodeId,
                TimestampUtc = DateTime.UtcNow,
                Databases = databaseStatuses
            };
        }

        private async Task<ReplicationDatabaseStatus> BuildDatabaseStatusAsync(string databaseName, CancellationToken cancellationToken)
        {
            try
            {
                LiteDbNodeStore store = await _logicalDatabaseStoreProvider.GetStoreAsync(databaseName, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ClusterPeer> peers = await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);
                long localMaxLogSequence = await GetLocalMaxLogSequenceAsync(store, cancellationToken).ConfigureAwait(false);
                List<ReplicationPeerStatus> peerStatuses = new List<ReplicationPeerStatus>(peers.Count);

                foreach (ClusterPeer peer in peers.OrderBy(x => x.NodeId, StringComparer.Ordinal))
                {
                    PeerCheckpointRecord checkpoint = await store.GetOrCreatePeerCheckpointAsync(_nodeOptions.NodeId, peer.NodeId, cancellationToken).ConfigureAwait(false);
                    long pendingPushOperations = peer.IsActive && !string.Equals(peer.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal)
                        ? await CountOperationsAfterLogSequenceAsync(store, checkpoint.LastPushedLocalLogSequence, cancellationToken).ConfigureAwait(false)
                        : 0;

                    peerStatuses.Add(new ReplicationPeerStatus
                    {
                        PeerNodeId = peer.NodeId,
                        BaseUrl = peer.BaseUrl,
                        IsActive = peer.IsActive,
                        LastPushedLocalLogSequence = checkpoint.LastPushedLocalLogSequence,
                        LastPulledPeerLogSequence = checkpoint.LastPulledPeerLogSequence,
                        LocalMaxLogSequence = localMaxLogSequence,
                        PendingPushOperations = pendingPushOperations,
                        UpdatedUtc = checkpoint.UpdatedUtc
                    });
                }

                int activePeerCount = peerStatuses.Count(x => x.IsActive && !string.Equals(x.PeerNodeId, _nodeOptions.NodeId, StringComparison.Ordinal));
                long totalPendingPushOperations = peerStatuses.Sum(x => x.PendingPushOperations);

                return new ReplicationDatabaseStatus
                {
                    DatabaseName = databaseName,
                    Status = "Healthy",
                    Error = null,
                    LocalMaxLogSequence = localMaxLogSequence,
                    ActivePeerCount = activePeerCount,
                    TotalPendingPushOperations = totalPendingPushOperations,
                    Peers = peerStatuses
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replication status inspection failed. Database={Database} NodeId={NodeId}", databaseName, _nodeOptions.NodeId);
                return new ReplicationDatabaseStatus
                {
                    DatabaseName = databaseName,
                    Status = "Error",
                    Error = ex.Message,
                    LocalMaxLogSequence = 0,
                    ActivePeerCount = 0,
                    TotalPendingPushOperations = 0,
                    Peers = Array.Empty<ReplicationPeerStatus>()
                };
            }
        }

        private static async Task<long> GetLocalMaxLogSequenceAsync(LiteDbNodeStore store, CancellationToken cancellationToken)
        {
            IReadOnlyList<OperationRecord> operations = await store.GetOperationsAfterLogSequenceAsync(0, MaxLagScanBatchSize, cancellationToken).ConfigureAwait(false);
            long maxLogSequence = 0;

            while (operations.Count > 0)
            {
                maxLogSequence = Math.Max(maxLogSequence, operations.Max(x => x.LogSequence));
                if (operations.Count < MaxLagScanBatchSize)
                {
                    break;
                }

                operations = await store.GetOperationsAfterLogSequenceAsync(maxLogSequence, MaxLagScanBatchSize, cancellationToken).ConfigureAwait(false);
            }

            return maxLogSequence;
        }

        private static async Task<long> CountOperationsAfterLogSequenceAsync(LiteDbNodeStore store, long afterLogSequence, CancellationToken cancellationToken)
        {
            long count = 0;
            long cursor = afterLogSequence;

            while (true)
            {
                IReadOnlyList<OperationRecord> operations = await store.GetOperationsAfterLogSequenceAsync(cursor, MaxLagScanBatchSize, cancellationToken).ConfigureAwait(false);
                if (operations.Count == 0)
                {
                    return count;
                }

                count += operations.Count;
                cursor = operations.Max(x => x.LogSequence);

                if (operations.Count < MaxLagScanBatchSize)
                {
                    return count;
                }
            }
        }
    }
}
