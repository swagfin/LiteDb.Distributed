using System.Text.Json;

namespace LiteDb.Distributed.Server.Core.Cache
{
    public class CacheGetResponse
    {
        public required string Key { get; set; }
        public required JsonElement Value { get; set; }
        public required DateTime CreatedUtc { get; set; }
        public required DateTime UpdatedUtc { get; set; }
        public required DateTime ExpiresAtUtc { get; set; }
        public required string RemainingTtl { get; set; }
    }
}
