using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IDocumentStateReader
{
    Task<DocumentState?> GetStateAsync(
        string collection,
        string entityId,
        CancellationToken cancellationToken = default);
}
