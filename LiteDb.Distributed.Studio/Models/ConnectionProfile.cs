namespace LiteDb.Distributed.Studio.Models
{
    public class ConnectionProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "http://localhost:1446";
        public string Database { get; set; } = "testapp";
        public string ApiKey { get; set; } = "root";
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public ConnectionProfile Clone()
        {
            return new ConnectionProfile
            {
                Id = Id,
                Name = Name,
                BaseUrl = BaseUrl,
                Database = Database,
                ApiKey = ApiKey,
                UpdatedUtc = UpdatedUtc
            };
        }

        public static ConnectionProfile CreateDefault() => new();
    }
}
