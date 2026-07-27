namespace LiteDb.Distributed.Server.Core.Context
{
    public interface IApiKeyAuthorizationService
    {
        string RootApiKey { get; }
        ApiKeyAccess Authorize(string apiKey, string databaseName);
    }
}
