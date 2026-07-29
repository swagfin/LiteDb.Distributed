namespace LiteDb.Distributed.Server.Core.Cache
{
    public class CacheSetResponse
    {
        public required string Key { get; set; }
        public required string Version { get; set; }
        public required DateTime CommittedUtc { get; set; }
        public required DateTime ExpiresAtUtc { get; set; }
        public required string Ttl { get; set; }
    }
}
