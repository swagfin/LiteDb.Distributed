

namespace LiteDb.Distributed.Infrastructure.Storage
{
    public interface ILogicalDatabaseCatalog
    {
        Task<LogicalDatabaseRegistration> GetOrCreateAsync(string databaseName, CancellationToken cancellationToken = default);
        Task<bool> ExistsAsync(string databaseName, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<LogicalDatabaseRegistration>> GetAllAsync(CancellationToken cancellationToken = default);
    }

}
