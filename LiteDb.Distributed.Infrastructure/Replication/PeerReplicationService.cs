using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace LiteDb.Distributed.Infrastructure.Replication;

public class PeerReplicationService : IClusterReplicationService
{
    private readonly string _localNodeId;
    private readonly int _batchSize;
    private readonly int _peerConcurrency;
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
        _peerConcurrency = Math.Clamp(options.ReplicationPeerConcurrency, 1, 32);

        _operationLogStore = operationLogStore ?? throw new ArgumentNullException(nameof(operationLogStore));
        _peerCheckpointStore = peerCheckpointStore ?? throw new ArgumentNullException(nameof(peerCheckpointStore));
        _clusterPeerRegistry = clusterPeerRegistry ?? throw new ArgumentNullException(nameof(clusterPeerRegistry));
        _peerClient = peerClient ?? throw new ArgumentNullException(nameof(peerClient));
        _operationIngestionService = operationIngestionService ?? throw new ArgumentNullException(nameof(operationIngestionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ReplicateOnceAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch cycleStopwatch = Stopwatch.StartNew();
        IReadOnlyList<ClusterPeer> peers = await _clusterPeerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);
        List<ClusterPeer> activePeers = peers.Where(x => x.IsActive && !string.Equals(x.NodeId, _localNodeId, StringComparison.Ordinal)).ToList();

        int maxConcurrency = Math.Min(_peerConcurrency, Math.Max(1, activePeers.Count));
        _logger.LogDebug("Replication cycle started. LocalNodeId={LocalNodeId} RegisteredPeers={RegisteredPeers} ActivePeers={ActivePeers} PeerConcurrency={PeerConcurrency}", _localNodeId, peers.Count, activePeers.Count, maxConcurrency);

        if (activePeers.Count == 0)
        {
            cycleStopwatch.Stop();
            _logger.LogDebug("Replication cycle completed. LocalNodeId={LocalNodeId} ActivePeers={ActivePeers} DurationMs={DurationMs}", _localNodeId, activePeers.Count, cycleStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        ConcurrentBag<string> failedPeers = new ConcurrentBag<string>();
        using SemaphoreSlim throttler = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        List<Task> tasks = activePeers.Select(peer => ReplicatePeerWithThrottleAsync(peer, throttler, failedPeers, cancellationToken)).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        cycleStopwatch.Stop();
        _logger.LogDebug("Replication cycle completed. LocalNodeId={LocalNodeId} ActivePeers={ActivePeers} DurationMs={DurationMs}", _localNodeId, activePeers.Count, cycleStopwatch.Elapsed.TotalMilliseconds);

        List<string> failedPeerList = failedPeers.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (failedPeerList.Count > 0)
        {
            throw new InvalidOperationException($"Peer replication failed for {failedPeerList.Count} peer(s): {string.Join(", ", failedPeerList)}.");
        }
    }

    private async Task ReplicatePeerWithThrottleAsync(ClusterPeer peer, SemaphoreSlim throttler, ConcurrentBag<string> failedPeers, CancellationToken cancellationToken)
    {
        await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplicatePeerAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Peer replication failed for {PeerNodeId} ({BaseUrl})", peer.NodeId, peer.BaseUrl);
            failedPeers.Add(peer.NodeId);
        }
        finally
        {
            throttler.Release();
        }
    }

    private async Task ReplicatePeerAsync(ClusterPeer peer, CancellationToken cancellationToken)
    {
        Stopwatch peerStopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Peer replication started. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} PeerBaseUrl={PeerBaseUrl}", _localNodeId, peer.NodeId, peer.BaseUrl);

        PeerCheckpointRecord checkpoint = await _peerCheckpointStore.GetOrCreatePeerCheckpointAsync(_localNodeId, peer.NodeId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<OperationRecord> pendingForPush = await _operationLogStore
            .GetOperationsAfterLogSequenceAsync(
                checkpoint.LastPushedLocalLogSequence,
                _batchSize,
                cancellationToken)
            .ConfigureAwait(false);

        int pushedCount = 0;
        int pushAcceptedCount = 0;

        if (pendingForPush.Count > 0)
        {
            pushedCount = pendingForPush.Count;
            ReplicationPushResponse pushResponse = await _peerClient
                .PushAsync(
                    peer,
                    new ReplicationPushRequest
                    {
                        SourceNodeId = _localNodeId,
                        Operations = pendingForPush
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            pushAcceptedCount = pushResponse.AcceptedCount;

            checkpoint = checkpoint with
            {
                LastPushedLocalLogSequence = pendingForPush.Max(x => x.LogSequence),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        int pulledCount = 0;
        int pulledAcceptedCount = 0;
        int pulledConflictCount = 0;

        ReplicationPullResponse pulled = await _peerClient
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

        pulledCount = pulled.Operations.Count;

        if (pulled.Operations.Count > 0)
        {
            OperationIngestionResult ingestionResult = await _operationIngestionService.IngestAsync(_localNodeId, pulled.Operations, cancellationToken).ConfigureAwait(false);

            pulledAcceptedCount = ingestionResult.AcceptedCount;
            pulledConflictCount = ingestionResult.ConflictCount;

            checkpoint = checkpoint with
            {
                LastPulledPeerLogSequence = Math.Max(
                    checkpoint.LastPulledPeerLogSequence,
                    pulled.Operations.Max(x => x.LogSequence)),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        await _peerCheckpointStore.SavePeerCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);

        peerStopwatch.Stop();

        LogLevel logLevel = pushedCount > 0 || pulledCount > 0 || pulledConflictCount > 0 ? LogLevel.Information : LogLevel.Debug;

        _logger.Log(logLevel, "Peer replication completed. LocalNodeId={LocalNodeId} PeerNodeId={PeerNodeId} Pushed={Pushed} PushAccepted={PushAccepted} Pulled={Pulled} PullAccepted={PullAccepted} PullConflicts={PullConflicts} CheckpointPush={CheckpointPush} CheckpointPull={CheckpointPull} DurationMs={DurationMs}", _localNodeId, peer.NodeId, pushedCount, pushAcceptedCount, pulledCount, pulledAcceptedCount, pulledConflictCount, checkpoint.LastPushedLocalLogSequence, checkpoint.LastPulledPeerLogSequence, peerStopwatch.Elapsed.TotalMilliseconds);
    }
}



