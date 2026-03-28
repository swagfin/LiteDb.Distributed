using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Infrastructure.Replication;

public sealed class OperationIngestionService : IOperationIngestionService
{
    private readonly IDocumentStateReader _documentStateReader;
    private readonly IConflictResolver _conflictResolver;
    private readonly IRemoteOperationApplier _remoteOperationApplier;
    private readonly IConflictStore _conflictStore;
    private readonly ILogger<OperationIngestionService> _logger;

    public OperationIngestionService(
        IDocumentStateReader documentStateReader,
        IConflictResolver conflictResolver,
        IRemoteOperationApplier remoteOperationApplier,
        IConflictStore conflictStore,
        ILogger<OperationIngestionService> logger)
    {
        _documentStateReader = documentStateReader ?? throw new ArgumentNullException(nameof(documentStateReader));
        _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
        _remoteOperationApplier = remoteOperationApplier ?? throw new ArgumentNullException(nameof(remoteOperationApplier));
        _conflictStore = conflictStore ?? throw new ArgumentNullException(nameof(conflictStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OperationIngestionResult> IngestAsync(
        string sourceNodeId,
        IReadOnlyList<OperationRecord> operations,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceNodeId))
        {
            throw new ArgumentException("Source node id is required.", nameof(sourceNodeId));
        }

        ArgumentNullException.ThrowIfNull(operations);

        var acceptedCount = 0;
        var conflictCount = 0;

        foreach (var operation in operations.OrderBy(x => x.LogSequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localState = await _documentStateReader
                .GetStateAsync(operation.Collection, operation.EntityId, cancellationToken)
                .ConfigureAwait(false);

            var decision = await _conflictResolver
                .ResolveAsync(
                    new ConflictResolutionContext
                    {
                        LocalNodeId = sourceNodeId,
                        IncomingOperation = operation,
                        LocalDocumentState = localState
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (decision.Action == ConflictResolutionAction.ApplyIncoming)
            {
                var applied = await _remoteOperationApplier
                    .ApplyRemoteOperationAsync(operation with { IsSynced = true }, cancellationToken)
                    .ConfigureAwait(false);

                if (applied)
                {
                    acceptedCount += 1;
                }

                continue;
            }

            if (decision.Action == ConflictResolutionAction.KeepLocalAndRecordConflict)
            {
                conflictCount += 1;

                await _conflictStore.RecordConflictAsync(
                        new ConflictRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            NodeId = sourceNodeId,
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

                _logger.LogWarning(
                    "Conflict recorded for {Collection}/{EntityId} from operation {OperationId}",
                    operation.Collection,
                    operation.EntityId,
                    operation.Id);
            }
        }

        return new OperationIngestionResult
        {
            AcceptedCount = acceptedCount,
            ConflictCount = conflictCount
        };
    }
}

