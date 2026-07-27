

namespace LiteDb.Distributed.Server.Domain.Models
{
    public class ReplicationPushResponse
    {
        public required int ProcessedCount { get; set; }
        public required int AcceptedCount { get; set; }
        public long LastProcessedLogSequence { get; set; }
    }

}
