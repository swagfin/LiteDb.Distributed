namespace LiteDb.Distributed.Server.Replication
{
    public class ReplicationStatusSnapshot
    {
        public required string NodeId { get; set; }
        public required DateTime TimestampUtc { get; set; }
        public IReadOnlyList<ReplicationDatabaseStatus> Databases { get; set; } = Array.Empty<ReplicationDatabaseStatus>();
    }

    public class ReplicationDatabaseStatus
    {
        public required string DatabaseName { get; set; }
        public required string Status { get; set; }
        public string? Error { get; set; }
        public long OldestAvailableLogSequence { get; set; }
        public long LocalMaxLogSequence { get; set; }
        public int ActivePeerCount { get; set; }
        public long TotalEstimatedPendingPushOperations { get; set; }
        public IReadOnlyList<ReplicationPeerStatus> Peers { get; set; } = Array.Empty<ReplicationPeerStatus>();
    }

    public class ReplicationPeerStatus
    {
        public required string PeerNodeId { get; set; }
        public required string BaseUrl { get; set; }
        public required bool IsActive { get; set; }
        public required string CatchUpStatus { get; set; }
        public string CatchUpReason { get; set; } = string.Empty;
        public long OldestAvailableLogSequence { get; set; }
        public long LastPushedLocalLogSequence { get; set; }
        public long LastPulledPeerLogSequence { get; set; }
        public long LocalMaxLogSequence { get; set; }
        public long EstimatedPendingPushOperations { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
