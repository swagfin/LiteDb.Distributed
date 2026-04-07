

namespace LiteDb.Distributed.Infrastructure.Storage
{
    public interface ILogicalDatabaseStoreProvider : IDisposable
    {
        Task<LiteDbNodeStore> GetCurrentStoreAsync(CancellationToken cancellationToken = default);

        Task<LiteDbNodeStore> GetStoreAsync(string databaseName, string credential, CancellationToken cancellationToken = default);
    }


}

