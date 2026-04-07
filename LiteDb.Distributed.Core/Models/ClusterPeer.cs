

namespace LiteDb.Distributed.Core.Models
{
    public sealed record ClusterPeer
    {
        public required string NodeId { get; init; }
        public required string BaseUrl { get; init; }
        public bool IsActive { get; init; } = true;
        public DateTime UpdatedUtc { get; init; }
    }
}
