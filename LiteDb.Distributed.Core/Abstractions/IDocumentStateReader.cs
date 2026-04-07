using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface IDocumentStateReader
    {
        Task<DocumentState?> GetStateAsync(string collection, string entityId, CancellationToken cancellationToken = default);
    }

}
