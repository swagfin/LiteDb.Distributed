using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Configuration
{
    public record ClusterNodeOptions
    {
        public required string NodeId { get; init; }
        public string ReplicationApiKey { get; init; } = "I_AM_ONE_OF_YOU";
        public string DataDirectory { get; init; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        public int ReplicationBatchSize { get; init; } = 1000;
        public int ReplicationPeerConcurrency { get; init; } = 4;
        public int CacheCleanupIntervalSeconds { get; init; } = 30;
        public int CacheCleanupBatchSize { get; init; } = 500;
        public int CacheCleanupMaxScanPages { get; init; } = 20;
        public string ConflictResolutionPolicy { get; init; } = "ApplyIncoming";
        public IReadOnlyList<ClusterPeer> SeedPeers { get; init; } = Array.Empty<ClusterPeer>();
    }

}
