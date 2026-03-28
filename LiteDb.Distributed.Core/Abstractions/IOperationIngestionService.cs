using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions;

public interface IOperationIngestionService
{
    Task<OperationIngestionResult> IngestAsync(
        string sourceNodeId,
        IReadOnlyList<OperationRecord> operations,
        CancellationToken cancellationToken = default);
}

