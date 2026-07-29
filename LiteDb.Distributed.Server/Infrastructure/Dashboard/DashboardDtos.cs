namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    public class DashboardOverviewDto
    {
        public required string NodeId { get; set; }
        public required DateTime TimestampUtc { get; set; }
        public required string DataRootPath { get; set; }
        public required string NodeDataPath { get; set; }
        public IReadOnlyList<DashboardNodeStatusDto> Nodes { get; set; } = Array.Empty<DashboardNodeStatusDto>();
        public IReadOnlyList<DashboardPeerConnectivityDto> PeerConnections { get; set; } = Array.Empty<DashboardPeerConnectivityDto>();
        public IReadOnlyList<DashboardDatabaseStatusDto> Databases { get; set; } = Array.Empty<DashboardDatabaseStatusDto>();
    }

    public class DashboardNodeStatusDto
    {
        public required string NodeId { get; set; }
        public required string BaseUrl { get; set; }
        public required bool IsOnline { get; set; }
        public required string Status { get; set; }
        public required string HttpStatus { get; set; }
        public required string WebSocketStatus { get; set; }
        public required double? HttpProbeDurationMs { get; set; }
        public required double? WebSocketProbeDurationMs { get; set; }
        public required string? Error { get; set; }
        public required DateTime LastCheckedUtc { get; set; }
    }

    public class DashboardLatencySampleDto
    {
        public required DateTime TimestampUtc { get; set; }
        public required double? HttpDurationMs { get; set; }
        public required double? WebSocketDurationMs { get; set; }
    }

    public class DashboardPeerConnectivityDto
    {
        public required string PeerNodeId { get; set; }
        public required string BaseUrl { get; set; }
        public required bool IsPeerActive { get; set; }
        public required string OverallStatus { get; set; }
        public required string HttpStatus { get; set; }
        public required string WebSocketStatus { get; set; }
        public required double? HttpProbeDurationMs { get; set; }
        public required double? WebSocketProbeDurationMs { get; set; }
        public required string? Error { get; set; }
        public required DateTime LastCheckedUtc { get; set; }
        public IReadOnlyList<DashboardLatencySampleDto> LatencyHistory { get; set; } = Array.Empty<DashboardLatencySampleDto>();
    }

    public class DashboardDatabaseStatusDto
    {
        public required string Name { get; set; }
        public required string Status { get; set; }
        public required string? Error { get; set; }
        public required DashboardFileStatusDto DatabaseFile { get; set; }
        public required int PeerCount { get; set; }
        public required long TotalEstimatedPendingPushOperations { get; set; }
        public IReadOnlyList<DashboardReplicationPeerStatusDto> ReplicationPeers { get; set; } = Array.Empty<DashboardReplicationPeerStatusDto>();
    }

    public class DashboardReplicationPeerStatusDto
    {
        public required string PeerNodeId { get; set; }
        public required string CatchUpStatus { get; set; }
        public required long EstimatedPendingPushOperations { get; set; }
    }

    public class DashboardFileStatusDto
    {
        public required string Path { get; set; }
        public required bool Exists { get; set; }
        public required long SizeBytes { get; set; }
        public required DateTime? LastWriteUtc { get; set; }
    }
}
