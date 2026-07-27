

namespace LiteDb.Distributed.Server.Core.Models
{
    public class ReplicationPushRequest
    {
        public required string SourceNodeId { get; set; }
        public required IReadOnlyList<OperationRecord> Operations { get; set; }
    }

}
