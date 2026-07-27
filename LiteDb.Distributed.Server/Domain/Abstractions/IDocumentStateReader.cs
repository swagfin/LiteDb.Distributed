using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IDocumentStateReader
    {
        Task<DocumentState?> GetStateAsync(string collection, string entityId, CancellationToken cancellationToken = default);
    }

}
