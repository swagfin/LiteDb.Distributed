using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IOperationIngestionService
    {
        Task<OperationIngestionResult> IngestAsync(string localNodeId, IReadOnlyList<OperationRecord> operations, CancellationToken cancellationToken = default);
    }

}
