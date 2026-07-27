

namespace LiteDb.Distributed.Server.Core.Models
{
    public class ReplicationPullResponse
    {
        public required IReadOnlyList<OperationRecord> Operations { get; set; }
    }

}
