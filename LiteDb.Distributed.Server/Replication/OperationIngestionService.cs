using LiteDb.Distributed.Server.Domain.Abstractions;
using LiteDb.Distributed.Server.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Replication
{
    public class OperationIngestionService : IOperationIngestionService
    {
        private readonly IDocumentStateReader _documentStateReader;
        private readonly IConflictResolver _conflictResolver;
        private readonly IRemoteOperationApplier _remoteOperationApplier;
        private readonly IConflictStore _conflictStore;
        private readonly ILogger<OperationIngestionService> _logger;

        public OperationIngestionService(IDocumentStateReader documentStateReader, IConflictResolver conflictResolver, IRemoteOperationApplier remoteOperationApplier, IConflictStore conflictStore, ILogger<OperationIngestionService> logger)
        {
            _documentStateReader = documentStateReader ?? throw new ArgumentNullException(nameof(documentStateReader));
            _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
            _remoteOperationApplier = remoteOperationApplier ?? throw new ArgumentNullException(nameof(remoteOperationApplier));
            _conflictStore = conflictStore ?? throw new ArgumentNullException(nameof(conflictStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OperationIngestionResult> IngestAsync(string localNodeId, IReadOnlyList<OperationRecord> operations, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localNodeId))
            {
                throw new ArgumentException("Local node id is required.", nameof(localNodeId));
            }

            ArgumentNullException.ThrowIfNull(operations);

            int acceptedCount = 0;
            int conflictCount = 0;
            Stopwatch batchStopwatch = Stopwatch.StartNew();

            _logger.LogDebug("Starting operation ingestion. LocalNodeId={LocalNodeId} IncomingOperationCount={IncomingOperationCount}", localNodeId, operations.Count);

            // Process in log-sequence order to keep conflict decisions deterministic across nodes.
            foreach (OperationRecord? operation in operations.OrderBy(x => x.LogSequence))
            {
                cancellationToken.ThrowIfCancellationRequested();

                DocumentState? localState = await _documentStateReader.GetStateAsync(operation.Collection, operation.EntityId, cancellationToken).ConfigureAwait(false);

                ConflictResolutionResult decision = await _conflictResolver
                    .ResolveAsync(
                        new ConflictResolutionContext
                        {
                            LocalNodeId = localNodeId,
                            IncomingOperation = operation,
                            LocalDocumentState = localState
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (decision.Action == ConflictResolutionAction.ApplyIncoming)
                {
                    Stopwatch applyStopwatch = Stopwatch.StartNew();

                    OperationRecord syncedOperation = new OperationRecord
                    {
                        Id = operation.Id,
                        NodeId = operation.NodeId,
                        TimestampUtc = operation.TimestampUtc,
                        Collection = operation.Collection,
                        EntityId = operation.EntityId,
                        OperationType = operation.OperationType,
                        Payload = operation.Payload,
                        Sequence = operation.Sequence,
                        LogSequence = operation.LogSequence,
                        ParentVersion = operation.ParentVersion,
                        GlobalSequence = operation.GlobalSequence,
                        IsSynced = true,
                        IsTombstone = operation.IsTombstone
                    };

                    bool applied = await _remoteOperationApplier.ApplyRemoteOperationAsync(syncedOperation, cancellationToken).ConfigureAwait(false);

                    applyStopwatch.Stop();

                    if (applied)
                    {
                        acceptedCount += 1;
                    }

                    _logger.LogInformation("Processed incoming operation. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} OperationId={OperationId} Collection={Collection} EntityId={EntityId} Applied={Applied} ApplyDurationMs={ApplyDurationMs}", localNodeId, operation.NodeId, operation.Id, operation.Collection, operation.EntityId, applied, applyStopwatch.Elapsed.TotalMilliseconds);

                    continue;
                }

                if (decision.Action == ConflictResolutionAction.KeepLocalAndRecordConflict)
                {
                    conflictCount += 1;

                    // Persist enough context for later reconciliation tooling or manual inspection.
                    await _conflictStore.RecordConflictAsync(
                            new ConflictRecord
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                NodeId = localNodeId,
                                Collection = operation.Collection,
                                EntityId = operation.EntityId,
                                IncomingOperationId = operation.Id,
                                LocalVersion = localState?.Version ?? string.Empty,
                                IncomingVersionHint = operation.ParentVersion ?? operation.Id,
                                Reason = decision.Reason,
                                CreatedUtc = DateTime.UtcNow,
                                LocalPayload = localState?.Payload,
                                IncomingPayload = operation.Payload
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogWarning("Conflict recorded for {Collection}/{EntityId} from operation {OperationId}. Reason={Reason}", operation.Collection, operation.EntityId, operation.Id, decision.Reason);

                    continue;
                }

                _logger.LogInformation("Skipped incoming operation after conflict resolution. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} OperationId={OperationId} Collection={Collection} EntityId={EntityId} Reason={Reason}", localNodeId, operation.NodeId, operation.Id, operation.Collection, operation.EntityId, decision.Reason);
            }

            batchStopwatch.Stop();
            int notAppliedCount = Math.Max(0, operations.Count - acceptedCount);

            _logger.LogInformation("Operation ingestion completed. LocalNodeId={LocalNodeId} Incoming={Incoming} Accepted={Accepted} Conflicts={Conflicts} NotApplied={NotApplied} DurationMs={DurationMs}", localNodeId, operations.Count, acceptedCount, conflictCount, notAppliedCount, batchStopwatch.Elapsed.TotalMilliseconds);

            return new OperationIngestionResult
            {
                AcceptedCount = acceptedCount,
                ConflictCount = conflictCount
            };
        }
    }
}
