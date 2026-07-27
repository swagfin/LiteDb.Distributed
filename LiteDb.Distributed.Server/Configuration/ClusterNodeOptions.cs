using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Configuration
{
    public class ClusterNodeOptions
    {
        public string NodeId { get; set; } = "node-1";
        public string ReplicationApiKey { get; set; } = "I_AM_ONE_OF_YOU";
        public string DataDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        public int ReplicationBatchSize { get; set; } = 1000;
        public int ReplicationPeerConcurrency { get; set; } = 4;
        public int ReplicationSignalAckTimeoutMilliseconds { get; set; } = 10000;
        public int CacheCleanupIntervalSeconds { get; set; } = 30;
        public int CacheCleanupBatchSize { get; set; } = 500;
        public int CacheCleanupMaxScanPages { get; set; } = 20;
        public bool OperationLogPruningEnabled { get; set; } = true;
        public int OperationLogRetentionDays { get; set; } = 7;
        public int OperationLogRetainRecentOperations { get; set; } = 10_000;
        public int OperationLogPruningIntervalMinutes { get; set; } = 60;
        public int OperationLogPruningBatchSize { get; set; } = 1_000;
        public int OperationReceiptRetentionDays { get; set; } = 90;
        public int OperationReceiptPruningBatchSize { get; set; } = 1_000;
        public string ConflictResolutionPolicy { get; set; } = "ApplyIncoming";
        public IReadOnlyList<ClusterPeer> SeedPeers { get; set; } = Array.Empty<ClusterPeer>();
    }

}
