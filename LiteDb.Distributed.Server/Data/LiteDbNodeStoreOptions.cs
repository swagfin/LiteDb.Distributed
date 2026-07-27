using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Data
{
    public class LiteDbNodeStoreOptions
    {
        public required string DatabaseName { get; set; }
        public required string DatabasePath { get; set; }
        public required string NodeId { get; set; }
        public IReadOnlyList<ClusterPeer> SeedPeers { get; set; } = Array.Empty<ClusterPeer>();
    }

}
