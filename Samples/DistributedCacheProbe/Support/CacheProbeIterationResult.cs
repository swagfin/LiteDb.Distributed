namespace DistributedCacheProbe.Support
{
    public class CacheProbeIterationResult
    {
        public string Key { get; set; } = string.Empty;
        public string WriterNodeName { get; set; } = string.Empty;
        public string WriterBaseUrl { get; set; } = string.Empty;
        public string Ttl { get; set; } = string.Empty;
        public bool WriteSucceeded { get; set; }
        public IReadOnlyList<ReplicationProbeResult> PeerResults { get; set; } = Array.Empty<ReplicationProbeResult>();

        public bool VisibleOnAllPeers => WriteSucceeded && PeerResults.Count > 0 && PeerResults.All(x => x.Found);

        public TimeSpan? AllPeersVisibleElapsed
        {
            get
            {
                if (!VisibleOnAllPeers)
                {
                    return null;
                }

                return PeerResults.Max(x => x.Elapsed);
            }
        }
    }
}
