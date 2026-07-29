using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IOperationLogStore
    {
        Task<IReadOnlyList<OperationRecord>> GetOperationsAfterLogSequenceAsync(long afterLogSequence, int batchSize, CancellationToken cancellationToken = default);

        Task<bool> ContainsOperationAsync(string operationId, CancellationToken cancellationToken = default);
    }
}
