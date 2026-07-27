namespace DistributedCacheProbe.Support
{
    public class CacheValue
    {
        public string Value { get; set; } = string.Empty;
        public string OriginNode { get; set; } = string.Empty;
        public DateTime WrittenUtc { get; set; }
        public string Ttl { get; set; } = string.Empty;
    }
}
