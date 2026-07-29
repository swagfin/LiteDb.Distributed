namespace LiteDb.Distributed.Server.Infrastructure.Replication.Signals
{
    internal class ReplicationSignalAck
    {
        public bool Accepted { get; set; }
        public string? Error { get; set; }
    }
}
