

namespace LiteDb.Distributed.Infrastructure.Storage
{
    public interface ILogicalDatabaseCatalog
    {
        Task<LogicalDatabaseRegistration> GetOrCreateAsync(string databaseName, string credential, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<LogicalDatabaseRegistration>> GetAllAsync(CancellationToken cancellationToken = default);
    }


}
