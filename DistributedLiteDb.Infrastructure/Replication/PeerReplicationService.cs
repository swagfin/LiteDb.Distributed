using DistributedLiteDb.Core.Abstractions;
using DistributedLiteDb.Core.Models;
using DistributedLiteDb.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace DistributedLiteDb.Infrastructure.Replication;

public sealed class PeerReplicationService : IClusterReplicationService
{
    private readonly string _localNodeId;
    private readonly int _batchSize;
    private readonly IOperationLogStore _operationLogStore;
    private readonly IPeerCheckpointStore _peerCheckpointStore;
    private readonly IClusterPeerRegistry _clusterPeerRegistry;
    private readonly IPeerReplicationClient _peerClient;
    private readonly IOperationIngestionService _operationIngestionService;
    private readonly ILogger<PeerReplicationService> _logger;

    public PeerReplicationService(
        ClusterNodeOptions options,
        IOperationLogStore operationLogStore,
        IPeerCheckpointStore peerCheckpointStore,
        IClusterPeerRegistry clusterPeerRegistry,
        IPeerReplicationClient peerClient,
        IOperationIngestionService operationIngestionService,
        ILogger<PeerReplicationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _localNodeId = options.NodeId;
        _batchSize = Math.Clamp(options.ReplicationBatchSize, 1, 10_000);

        _operationLogStore = operationLogStore ?? throw new ArgumentNullException(nameof(operationLogStore));
        _peerCheckpointStore = peerCheckpointStore ?? throw new ArgumentNullException(nameof(peerCheckpointStore));
        _clusterPeerRegistry = clusterPeerRegistry ?? throw new ArgumentNullException(nameof(clusterPeerRegistry));
        _peerClient = peerClient ?? throw new ArgumentNullException(nameof(peerClient));
        _operationIngestionService = operationIngestionService ?? throw new ArgumentNullException(nameof(operationIngestionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReplicateOnceAsync(CancellationToken cancellationToken = default)
    {
        var peers = await _clusterPeerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);

        foreach (var peer in peers.Where(x => x.IsActive && !string.Equals(x.NodeId, _localNodeId, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ReplicatePeerAsync(peer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // TODO: Add circuit-breaker/backoff per peer for noisy network partitions.
                _logger.LogWarning(ex, "Peer replication failed for {PeerNodeId} ({BaseUrl})", peer.NodeId, peer.BaseUrl);
            }
        }
    }

    private async Task ReplicatePeerAsync(ClusterPeer peer, CancellationToken cancellationToken)
    {
        var checkpoint = await _peerCheckpointStore
            .GetOrCreatePeerCheckpointAsync(_localNodeId, peer.NodeId, cancellationToken)
            .ConfigureAwait(false);

        var pendingForPush = await _operationLogStore
            .GetOperationsAfterLogSequenceAsync(
                checkpoint.LastPushedLocalLogSequence,
                _batchSize,
                cancellationToken)
            .ConfigureAwait(false);

        if (pendingForPush.Count > 0)
        {
            await _peerClient
                .PushAsync(
                    peer,
                    new ReplicationPushRequest
                    {
                        SourceNodeId = _localNodeId,
                        Operations = pendingForPush
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            checkpoint = checkpoint with
            {
                LastPushedLocalLogSequence = pendingForPush.Max(x => x.LogSequence),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        var pulled = await _peerClient
            .PullAsync(
                peer,
                new ReplicationPullRequest
                {
                    RequestingNodeId = _localNodeId,
                    AfterLogSequence = checkpoint.LastPulledPeerLogSequence,
                    BatchSize = _batchSize
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (pulled.Operations.Count > 0)
        {
            await _operationIngestionService
                .IngestAsync(_localNodeId, pulled.Operations, cancellationToken)
                .ConfigureAwait(false);

            checkpoint = checkpoint with
            {
                LastPulledPeerLogSequence = Math.Max(
                    checkpoint.LastPulledPeerLogSequence,
                    pulled.Operations.Max(x => x.LogSequence)),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        await _peerCheckpointStore.SavePeerCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }
}
