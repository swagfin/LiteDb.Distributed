

namespace LiteDb.Distributed.Core.Models
{
    public sealed record OperationRecord
    {
        public required string Id { get; init; }
        public required string NodeId { get; init; }
        public required DateTime TimestampUtc { get; init; }
        public required string Collection { get; init; }
        public required string EntityId { get; init; }
        public required OperationType OperationType { get; init; }
        public required string Payload { get; init; }
        public required long Sequence { get; init; }
        public long LogSequence { get; init; }
        public string? ParentVersion { get; init; }
        public long? GlobalSequence { get; init; }
        public bool IsSynced { get; init; }
        public bool IsTombstone { get; init; }
    }


}
