namespace LiteDb.Distributed.Server.Replication
{
    public interface IOperationLogPruningService
    {
        Task<IReadOnlyList<OperationLogPruningDatabaseResult>> PruneOnceAsync(CancellationToken cancellationToken = default);
    }
}
