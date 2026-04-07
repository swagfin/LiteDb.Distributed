

namespace LiteDb.Distributed.Core.Models
{
    public sealed record ReplicationPushRequest
    {
        public required string SourceNodeId { get; init; }
        public required IReadOnlyList<OperationRecord> Operations { get; init; }
    }


}

