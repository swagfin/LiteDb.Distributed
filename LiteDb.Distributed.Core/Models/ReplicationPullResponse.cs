

namespace LiteDb.Distributed.Core.Models
{
    public class ReplicationPullResponse
    {
        public required IReadOnlyList<OperationRecord> Operations { get; set; }
    }

}
