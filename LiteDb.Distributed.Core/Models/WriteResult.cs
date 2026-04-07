

namespace LiteDb.Distributed.Core.Models
{
    public sealed record WriteResult
    {
        public required string Collection { get; init; }
        public required string EntityId { get; init; }
        public required string Version { get; init; }
        public required DateTime CommittedUtc { get; init; }
        public required bool IsDeleted { get; init; }
        public required OperationRecord Operation { get; init; }
    }


}

