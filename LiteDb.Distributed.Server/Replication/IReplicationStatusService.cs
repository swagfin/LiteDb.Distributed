namespace LiteDb.Distributed.Server.Replication
{
    public interface IReplicationStatusService
    {
        Task<ReplicationStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    }
}
