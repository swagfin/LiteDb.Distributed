

namespace LiteDb.Distributed.Server.Domain.Models
{
    public class ReplicationPullResponse
    {
        public required IReadOnlyList<OperationRecord> Operations { get; set; }
    }

}
