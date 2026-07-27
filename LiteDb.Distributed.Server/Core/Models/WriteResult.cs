

namespace LiteDb.Distributed.Server.Core.Models
{
    public class WriteResult
    {
        public required string Collection { get; set; }
        public required string EntityId { get; set; }
        public required string Version { get; set; }
        public required DateTime CommittedUtc { get; set; }
        public required bool IsDeleted { get; set; }
        public required OperationRecord Operation { get; set; }
    }
}
