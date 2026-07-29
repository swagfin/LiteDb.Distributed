namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    public class DashboardPeerProbeResult
    {
        public DashboardPeerProbeResult(DashboardNodeStatusDto nodeStatus, DashboardPeerConnectivityDto peerConnectivity)
        {
            NodeStatus = nodeStatus;
            PeerConnectivity = peerConnectivity;
        }

        public DashboardNodeStatusDto NodeStatus { get; set; }
        public DashboardPeerConnectivityDto PeerConnectivity { get; set; }
    }
}
