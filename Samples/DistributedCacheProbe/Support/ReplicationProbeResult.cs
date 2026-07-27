namespace DistributedCacheProbe.Support
{
    public class ReplicationProbeResult
    {
        public ReplicationProbeResult(string nodeName, bool found, TimeSpan elapsed)
        {
            NodeName = nodeName;
            Found = found;
            Elapsed = elapsed;
        }

        public string NodeName { get; set; }
        public bool Found { get; set; }
        public TimeSpan Elapsed { get; set; }
    }
}
