namespace LiteDb.Distributed.Infrastructure.Context
{
    public interface IApiKeyAuthorizationService
    {
        string RootApiKey { get; }
        ApiKeyAccess Authorize(string apiKey, string databaseName);
    }
}
