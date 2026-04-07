namespace LiteDb.Distributed.Core.Abstractions;

public interface ILocalDocumentReader
{
    Task<TDocument?> GetByIdAsync<TDocument>(string collection, string entityId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TDocument>> ListAsync<TDocument>(string collection, int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TDocument>> ExecuteQueryAsync<TDocument>(string query, int take = 100, CancellationToken cancellationToken = default);
}


