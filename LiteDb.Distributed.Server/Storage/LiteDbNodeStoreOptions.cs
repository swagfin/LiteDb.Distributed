using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Storage
{
    public class LiteDbNodeStoreOptions
    {
        public required string DatabaseName { get; set; }
        public required string DatabasePath { get; set; }
        public required string NodeId { get; set; }
        public IReadOnlyList<ClusterPeer> SeedPeers { get; set; } = Array.Empty<ClusterPeer>();
    }

}
