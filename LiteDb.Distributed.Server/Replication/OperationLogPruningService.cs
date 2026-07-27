using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Server.Replication
{
    public class OperationLogPruningService : BackgroundService, IOperationLogPruningService
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
        private readonly ILogger<OperationLogPruningService> _logger;
        private readonly TimeSpan _retentionAge;
        private readonly TimeSpan _receiptRetentionAge;
        private readonly TimeSpan _interval;
        private readonly int _retainRecentOperations;
        private readonly int _batchSize;
        private readonly int _receiptBatchSize;

        public OperationLogPruningService(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider, ILogger<OperationLogPruningService> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _logicalDatabaseStoreProvider = logicalDatabaseStoreProvider ?? throw new ArgumentNullException(nameof(logicalDatabaseStoreProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _retentionAge = TimeSpan.FromDays(Math.Clamp(_nodeOptions.OperationLogRetentionDays, 1, 3650));
            _receiptRetentionAge = TimeSpan.FromDays(Math.Clamp(_nodeOptions.OperationReceiptRetentionDays, 1, 3650));
            _interval = TimeSpan.FromMinutes(Math.Clamp(_nodeOptions.OperationLogPruningIntervalMinutes, 1, 1440));
            _retainRecentOperations = Math.Clamp(_nodeOptions.OperationLogRetainRecentOperations, 0, 1_000_000);
            _batchSize = Math.Clamp(_nodeOptions.OperationLogPruningBatchSize, 1, 10_000);
            _receiptBatchSize = Math.Clamp(_nodeOptions.OperationReceiptPruningBatchSize, 1, 10_000);
        }

        public async Task<IReadOnlyList<OperationLogPruningDatabaseResult>> PruneOnceAsync(CancellationToken cancellationToken = default)
        {
            if (!_nodeOptions.OperationLogPruningEnabled)
            {
                return Array.Empty<OperationLogPruningDatabaseResult>();
            }

            IReadOnlyList<LogicalDatabaseRegistration> databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            List<OperationLogPruningDatabaseResult> results = new List<OperationLogPruningDatabaseResult>(databases.Count);

            foreach (LogicalDatabaseRegistration database in databases.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
            {
                results.Add(await PruneDatabaseAsync(database.DatabaseName, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_nodeOptions.OperationLogPruningEnabled)
            {
                _logger.LogInformation("Operation log pruning worker is disabled.");
                return;
            }

            _logger.LogInformation("Operation log pruning worker started. IntervalMinutes={IntervalMinutes} RetentionDays={RetentionDays} ReceiptRetentionDays={ReceiptRetentionDays} RetainRecentOperations={RetainRecentOperations}", _interval.TotalMinutes, _retentionAge.TotalDays, _receiptRetentionAge.TotalDays, _retainRecentOperations);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PruneOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Operation log pruning iteration failed.");
                }

                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
        }

        private async Task<OperationLogPruningDatabaseResult> PruneDatabaseAsync(string databaseName, CancellationToken cancellationToken)
        {
            try
            {
                LiteDbNodeStore store = await _logicalDatabaseStoreProvider.GetStoreAsync(databaseName, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ClusterPeer> activePeers = (await store.GetPeersAsync(cancellationToken).ConfigureAwait(false))
                    .Where(x => x.IsActive && !string.Equals(x.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal))
                    .ToList();

                OperationReceiptPruneResult receiptPruneResult = await PruneReceiptsAsync(store, cancellationToken).ConfigureAwait(false);

                if (activePeers.Count == 0)
                {
                    return Skipped(databaseName, "No active peers are registered.", receiptPruneResult.PrunedCount);
                }

                List<PeerCheckpointRecord> checkpoints = new List<PeerCheckpointRecord>(activePeers.Count);
                foreach (ClusterPeer peer in activePeers)
                {
                    checkpoints.Add(await store.GetOrCreatePeerCheckpointAsync(_nodeOptions.NodeId, peer.NodeId, cancellationToken).ConfigureAwait(false));
                }

                long minimumPushedSequence = checkpoints.Min(x => x.LastPushedLocalLogSequence);
                long pruneThroughLogSequence = minimumPushedSequence - _retainRecentOperations;
                if (pruneThroughLogSequence <= 0)
                {
                    return Skipped(databaseName, "Active peer checkpoints have not advanced beyond the retained operation window.", receiptPruneResult.PrunedCount);
                }

                DateTime olderThanUtc = DateTime.UtcNow.Subtract(_retentionAge);
                OperationLogPruneResult pruneResult = await store.PruneOperationLogAsync(pruneThroughLogSequence, olderThanUtc, _batchSize, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Operation log pruning completed. Database={Database} PruneThroughLogSequence={PruneThroughLogSequence} Pruned={Pruned} MaxPrunedLogSequence={MaxPrunedLogSequence} PrunedReceipts={PrunedReceipts}", databaseName, pruneThroughLogSequence, pruneResult.PrunedCount, pruneResult.MaxPrunedLogSequence, receiptPruneResult.PrunedCount);

                return new OperationLogPruningDatabaseResult
                {
                    DatabaseName = databaseName,
                    Status = "Pruned",
                    Reason = null,
                    PruneThroughLogSequence = pruneThroughLogSequence,
                    PrunedCount = pruneResult.PrunedCount,
                    MaxPrunedLogSequence = pruneResult.MaxPrunedLogSequence,
                    PrunedReceiptCount = receiptPruneResult.PrunedCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Operation log pruning failed for database. Database={Database}", databaseName);
                return new OperationLogPruningDatabaseResult
                {
                    DatabaseName = databaseName,
                    Status = "Error",
                    Reason = ex.Message,
                    PruneThroughLogSequence = 0,
                    PrunedCount = 0,
                    MaxPrunedLogSequence = 0,
                    PrunedReceiptCount = 0
                };
            }
        }

        private async Task<OperationReceiptPruneResult> PruneReceiptsAsync(LiteDbNodeStore store, CancellationToken cancellationToken)
        {
            DateTime olderThanPrunedUtc = DateTime.UtcNow.Subtract(_receiptRetentionAge);
            return await store.PruneOperationReceiptsAsync(olderThanPrunedUtc, _receiptBatchSize, cancellationToken).ConfigureAwait(false);
        }

        private static OperationLogPruningDatabaseResult Skipped(string databaseName, string reason, int prunedReceiptCount)
        {
            return new OperationLogPruningDatabaseResult
            {
                DatabaseName = databaseName,
                Status = "Skipped",
                Reason = reason,
                PruneThroughLogSequence = 0,
                PrunedCount = 0,
                MaxPrunedLogSequence = 0,
                PrunedReceiptCount = prunedReceiptCount
            };
        }
    }
}
