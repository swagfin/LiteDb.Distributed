

namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public interface IReplicationSignalPublisher
    {
        void NotifyLocalChange(string reason);
    }

}
