

namespace LiteDb.Distributed.Server.Domain.Models
{
    public class ReplicationPushRequest
    {
        public required string SourceNodeId { get; set; }
        public required IReadOnlyList<OperationRecord> Operations { get; set; }
    }

}
