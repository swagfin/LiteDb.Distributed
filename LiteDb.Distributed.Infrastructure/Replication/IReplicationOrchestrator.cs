namespace LiteDb.Distributed.Infrastructure.Replication;

public interface IReplicationOrchestrator
{
    Task ReplicateAllDatabasesAsync(string reason, CancellationToken cancellationToken = default);
    Task ReplicateDatabaseAsync(string databaseName, string credential, string reason, CancellationToken cancellationToken = default);
}
