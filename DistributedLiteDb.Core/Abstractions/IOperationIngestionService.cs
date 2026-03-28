using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IOperationIngestionService
{
    Task<OperationIngestionResult> IngestAsync(
        string sourceNodeId,
        IReadOnlyList<OperationRecord> operations,
        CancellationToken cancellationToken = default);
}
