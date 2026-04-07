

namespace LiteDb.Distributed.Core.Models
{
    public sealed record ReplicationPullRequest
    {
        public required string RequestingNodeId { get; init; }
        public long AfterLogSequence { get; init; }
        public int BatchSize { get; init; } = 200;
    }


}
