namespace LiteDb.Distributed.Server.Infrastructure.Replication.Signals
{
    internal class ReplicationSignalMessage
    {
        public string Type { get; set; } = string.Empty;
        public string SourceNodeId { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public string ReplicationApiKey { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
