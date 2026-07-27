namespace LiteDb.Distributed.Server.Replication
{
    public interface IReplicationOrchestrator
    {
        Task ReplicateAllDatabasesAsync(string reason, CancellationToken cancellationToken = default);
        Task ReplicateDatabaseAsync(string databaseName, string apiKey, string reason, CancellationToken cancellationToken = default);
    }
}
