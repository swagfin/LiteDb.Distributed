namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public interface IReplicationStatusService
    {
        Task<ReplicationStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    }
}
