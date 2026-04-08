

namespace LiteDb.Distributed.Infrastructure.Replication
{
    public interface IReplicationSignalPublisher
    {
        void NotifyLocalChange(string reason);
    }

}
