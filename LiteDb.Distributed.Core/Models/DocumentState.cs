

namespace LiteDb.Distributed.Core.Models
{
    public sealed record DocumentState
    {
        public required string Collection { get; init; }
        public required string EntityId { get; init; }
        public required string Version { get; init; }
        public required string LastWriterNodeId { get; init; }
        public required DateTime LastModifiedUtc { get; init; }
        public required bool IsDeleted { get; init; }
        public required string Payload { get; init; }
    }


}

