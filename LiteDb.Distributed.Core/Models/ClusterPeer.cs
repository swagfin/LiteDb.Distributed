

namespace LiteDb.Distributed.Core.Models
{
    public class ClusterPeer
    {
        public required string NodeId { get; set; }
        public required string BaseUrl { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime UpdatedUtc { get; set; }
    }
}
