using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Data;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public class ReplicationStatusService : IReplicationStatusService
    {
        private const string ReadyStatus = "Ready";
        private const string CatchingUpStatus = "CatchingUp";
        private const string TooOldNeedsSnapshotStatus = "TooOldNeedsSnapshot";
        private const string InactiveStatus = "Inactive";

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
                OperationLogBounds bounds = await store.GetOperationLogBoundsAsync(cancellationToken).ConfigureAwait(false);
                List<ReplicationPeerStatus> peerStatuses = new List<ReplicationPeerStatus>(peers.Count);

                foreach (ClusterPeer peer in peers.OrderBy(x => x.NodeId, StringComparer.Ordinal))
                {
                    PeerCheckpointRecord checkpoint = await store.GetOrCreatePeerCheckpointAsync(_nodeOptions.NodeId, peer.NodeId, cancellationToken).ConfigureAwait(false);
                    ReplicationCatchUpState catchUpState = GetCatchUpState(peer, checkpoint, bounds);

                    peerStatuses.Add(new ReplicationPeerStatus
                    {
                        PeerNodeId = peer.NodeId,
                        BaseUrl = peer.BaseUrl,
                        IsActive = peer.IsActive,
                        CatchUpStatus = catchUpState.Status,
                        CatchUpReason = catchUpState.Reason,
                        OldestAvailableLogSequence = bounds.OldestLogSequence,
                        LastPushedLocalLogSequence = checkpoint.LastPushedLocalLogSequence,
                        LastPulledPeerLogSequence = checkpoint.LastPulledPeerLogSequence,
                        LocalMaxLogSequence = bounds.NewestLogSequence,
                        EstimatedPendingPushOperations = catchUpState.EstimatedPendingPushOperations,
                        UpdatedUtc = checkpoint.UpdatedUtc
                    });
                }

                int activePeerCount = peerStatuses.Count(x => x.IsActive && !string.Equals(x.PeerNodeId, _nodeOptions.NodeId, StringComparison.Ordinal));
                long totalEstimatedPendingPushOperations = peerStatuses.Sum(x => x.EstimatedPendingPushOperations);

                return new ReplicationDatabaseStatus
                {
                    DatabaseName = databaseName,
                    Status = "Healthy",
                    Error = null,
                    OldestAvailableLogSequence = bounds.OldestLogSequence,
                    LocalMaxLogSequence = bounds.NewestLogSequence,
                    ActivePeerCount = activePeerCount,
                    TotalEstimatedPendingPushOperations = totalEstimatedPendingPushOperations,
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
                    OldestAvailableLogSequence = 0,
                    LocalMaxLogSequence = 0,
                    ActivePeerCount = 0,
                    TotalEstimatedPendingPushOperations = 0,
                    Peers = Array.Empty<ReplicationPeerStatus>()
                };
            }
        }

        private static ReplicationCatchUpState GetCatchUpState(ClusterPeer peer, PeerCheckpointRecord checkpoint, OperationLogBounds bounds)
        {
            if (!peer.IsActive)
            {
                return new ReplicationCatchUpState(InactiveStatus, "Peer is inactive.", 0);
            }

            if (!bounds.HasOperations)
            {
                return new ReplicationCatchUpState(ReadyStatus, "No local operations are available.", 0);
            }

            if (checkpoint.LastPushedLocalLogSequence >= bounds.NewestLogSequence)
            {
                return new ReplicationCatchUpState(ReadyStatus, "Peer push checkpoint is current.", 0);
            }

            long nextRequiredLogSequence = checkpoint.LastPushedLocalLogSequence + 1;
            if (nextRequiredLogSequence < bounds.OldestLogSequence)
            {
                string reason = $"Peer requires log sequence {nextRequiredLogSequence}, but oldest available local operation is {bounds.OldestLogSequence}. Restore from snapshot before normal catch-up can continue.";
                return new ReplicationCatchUpState(TooOldNeedsSnapshotStatus, reason, Math.Max(0, bounds.NewestLogSequence - checkpoint.LastPushedLocalLogSequence));
            }

            long estimatedPending = Math.Max(0, bounds.NewestLogSequence - checkpoint.LastPushedLocalLogSequence);
            return new ReplicationCatchUpState(CatchingUpStatus, "Peer can catch up from the local operation log.", estimatedPending);
        }

        private class ReplicationCatchUpState
        {
            public ReplicationCatchUpState(string status, string reason, long estimatedPendingPushOperations)
            {
                Status = status;
                Reason = reason;
                EstimatedPendingPushOperations = estimatedPendingPushOperations;
            }

            public string Status { get; set; }
            public string Reason { get; set; }
            public long EstimatedPendingPushOperations { get; set; }
        }
    }
}
