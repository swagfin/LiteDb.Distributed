namespace LiteDb.Distributed.Server.Data
{
    public interface ILogicalDatabaseStoreProvider : IDisposable
    {
        Task<LiteDbNodeStore> GetCurrentStoreAsync(CancellationToken cancellationToken = default);
        Task<LiteDbNodeStore> GetStoreAsync(string databaseName, CancellationToken cancellationToken = default);
    }
}
