namespace LiteDb.Distributed.Studio.Models
{
    public class DashboardOverviewDto
    {
        public string NodeId { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public string DataRootPath { get; set; } = string.Empty;
        public string NodeDataPath { get; set; } = string.Empty;
        public List<DashboardNodeStatusDto> Nodes { get; set; } = [];
        public List<DashboardPeerConnectivityDto> PeerConnections { get; set; } = [];
        public List<DashboardDatabaseStatusDto> Databases { get; set; } = [];
    }

    public class DashboardNodeStatusDto
    {
        public string NodeId { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
        public string Status { get; set; } = string.Empty;
        public string HttpStatus { get; set; } = string.Empty;
        public string WebSocketStatus { get; set; } = string.Empty;
        public double? HttpProbeDurationMs { get; set; }
        public double? WebSocketProbeDurationMs { get; set; }
        public string? Error { get; set; }
        public DateTime LastCheckedUtc { get; set; }
    }

    public class DashboardPeerConnectivityDto
    {
        public string PeerNodeId { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public bool IsPeerActive { get; set; }
        public string OverallStatus { get; set; } = string.Empty;
        public string HttpStatus { get; set; } = string.Empty;
        public string WebSocketStatus { get; set; } = string.Empty;
        public double? HttpProbeDurationMs { get; set; }
        public double? WebSocketProbeDurationMs { get; set; }
        public string? Error { get; set; }
        public DateTime LastCheckedUtc { get; set; }
    }

    public class DashboardDatabaseStatusDto
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public DashboardFileStatusDto? DatabaseFile { get; set; }
        public int PeerCount { get; set; }
        public long LocalMaxLogSequence { get; set; }
        public long TotalPendingPushOperations { get; set; }
        public List<ReplicationPeerStatusDto> ReplicationPeers { get; set; } = [];
    }

    public class ReplicationPeerStatusDto
    {
        public string PeerNodeId { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public long LastPushedLocalLogSequence { get; set; }
        public long LastPulledPeerLogSequence { get; set; }
        public long LocalMaxLogSequence { get; set; }
        public long PendingPushOperations { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public class DashboardFileStatusDto
    {
        public string Path { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? LastWriteUtc { get; set; }
    }
}
