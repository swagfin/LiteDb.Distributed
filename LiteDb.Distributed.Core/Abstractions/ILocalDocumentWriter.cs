using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface ILocalDocumentWriter
    {
        Task<WriteResult> UpsertAsync<TDocument>(string collection, string entityId, TDocument document, string? parentVersion = null, CancellationToken cancellationToken = default);

        Task<WriteResult> DeleteAsync(string collection, string entityId, string? parentVersion = null, CancellationToken cancellationToken = default);

        Task EnsureCollectionAsync(string collection, CancellationToken cancellationToken = default);
    }

}
