using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Configuration
{
    public class ClusterNodeOptions
    {
        public required string NodeId { get; set; }
        public string ReplicationApiKey { get; set; } = "I_AM_ONE_OF_YOU";
        public string DataDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        public int ReplicationBatchSize { get; set; } = 1000;
        public int ReplicationPeerConcurrency { get; set; } = 4;
        public int ReplicationSignalAckTimeoutMilliseconds { get; set; } = 10000;
        public int CacheCleanupIntervalSeconds { get; set; } = 30;
        public int CacheCleanupBatchSize { get; set; } = 500;
        public int CacheCleanupMaxScanPages { get; set; } = 20;
        public string ConflictResolutionPolicy { get; set; } = "ApplyIncoming";
        public IReadOnlyList<ClusterPeer> SeedPeers { get; set; } = Array.Empty<ClusterPeer>();
    }

}
