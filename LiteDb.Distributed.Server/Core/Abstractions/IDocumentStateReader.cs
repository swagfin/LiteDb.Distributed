using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IDocumentStateReader
    {
        Task<DocumentState?> GetStateAsync(string collection, string entityId, CancellationToken cancellationToken = default);
    }

}
