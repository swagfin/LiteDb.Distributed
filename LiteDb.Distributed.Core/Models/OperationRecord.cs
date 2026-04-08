

namespace LiteDb.Distributed.Core.Models
{
    public class OperationRecord
    {
        public required string Id { get; set; }
        public required string NodeId { get; set; }
        public required DateTime TimestampUtc { get; set; }
        public required string Collection { get; set; }
        public required string EntityId { get; set; }
        public required OperationType OperationType { get; set; }
        public required string Payload { get; set; }
        public required long Sequence { get; set; }
        public long LogSequence { get; set; }
        public string? ParentVersion { get; set; }
        public long? GlobalSequence { get; set; }
        public bool IsSynced { get; set; }
        public bool IsTombstone { get; set; }
    }

}
