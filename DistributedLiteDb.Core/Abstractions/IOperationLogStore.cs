using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IOperationLogStore
{
    Task AppendOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationRecord>> GetOperationsAfterLogSequenceAsync(
        long afterLogSequence,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationRecord>> GetLocalOperationsAfterSequenceAsync(
        string nodeId,
        long afterSequence,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<bool> ContainsOperationAsync(string operationId, CancellationToken cancellationToken = default);
}
