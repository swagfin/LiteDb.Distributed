namespace LiteDb.Distributed.Server.Domain.Models
{
    public class ReplicationPullRequest
    {
        public required string RequestingNodeId { get; set; }
        public long AfterLogSequence { get; set; }
        public int BatchSize { get; set; } = 200;
    }
}
