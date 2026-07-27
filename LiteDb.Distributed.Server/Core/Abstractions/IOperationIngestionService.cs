using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IOperationIngestionService
    {
        Task<OperationIngestionResult> IngestAsync(string localNodeId, IReadOnlyList<OperationRecord> operations, CancellationToken cancellationToken = default);
    }

}
