namespace LiteDb.Distributed.Server.Context
{
    public class ApiKeyAuthorizationOptions
    {
        public string RootApiKey { get; set; } = "root";
        public List<ApiKeyEntryOptions> ApiKeys { get; set; } = new();
    }

    public class ApiKeyEntryOptions
    {
        public string Name { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public List<string> Databases { get; set; } = new();
        public Dictionary<string, bool> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
