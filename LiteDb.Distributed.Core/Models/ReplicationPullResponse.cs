

namespace LiteDb.Distributed.Core.Models
{
    public sealed record ReplicationPullResponse
    {
        public required IReadOnlyList<OperationRecord> Operations { get; init; }
    }


}
