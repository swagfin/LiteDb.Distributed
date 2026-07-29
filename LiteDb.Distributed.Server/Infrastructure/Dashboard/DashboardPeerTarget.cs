namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    public class DashboardPeerTarget
    {
        public DashboardPeerTarget(string nodeId, string baseUrl, bool isActive)
        {
            NodeId = nodeId;
            BaseUrl = baseUrl;
            IsActive = isActive;
        }

        public string NodeId { get; set; }
        public string BaseUrl { get; set; }
        public bool IsActive { get; set; }
    }
}
