using System.Text.Json;

namespace LiteDb.Distributed.Server.Core.Cache
{
    public class CacheEntryDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
}
