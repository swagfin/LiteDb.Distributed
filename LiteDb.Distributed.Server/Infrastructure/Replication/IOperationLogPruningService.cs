namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public interface IOperationLogPruningService
    {
        Task<IReadOnlyList<OperationLogPruningDatabaseResult>> PruneOnceAsync(CancellationToken cancellationToken = default);
    }
}
