

namespace LiteDb.Distributed.Server.Replication
{
    public interface IReplicationSignalPublisher
    {
        void NotifyLocalChange(string reason);
    }

}
