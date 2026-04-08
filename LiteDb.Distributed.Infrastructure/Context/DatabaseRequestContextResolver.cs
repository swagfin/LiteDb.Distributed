using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Infrastructure.Context
{
    public class DatabaseRequestContextResolver : IDatabaseRequestContextResolver
    {
        private const string DatabaseHeader = "Database";
        private const string ApiKeyHeader = "ApiKey";

        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly IApiKeyAuthorizationService _apiKeyAuthorizationService;
        private readonly ILogger<DatabaseRequestContextResolver> _logger;

        public DatabaseRequestContextResolver(ILogicalDatabaseCatalog logicalDatabaseCatalog, IApiKeyAuthorizationService apiKeyAuthorizationService, ILogger<DatabaseRequestContextResolver> logger)
        {
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _apiKeyAuthorizationService = apiKeyAuthorizationService ?? throw new ArgumentNullException(nameof(apiKeyAuthorizationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<DatabaseRequestContext> ResolveAsync(IHeaderDictionary headers, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(headers);

            string rawDatabaseName = headers[DatabaseHeader].ToString();
            string normalizedDatabaseName = DatabaseNameNormalizer.Normalize(rawDatabaseName);
            string apiKey = headers[ApiKeyHeader].ToString();
            ApiKeyAccess access = _apiKeyAuthorizationService.Authorize(apiKey, normalizedDatabaseName);

            bool exists = await _logicalDatabaseCatalog.ExistsAsync(normalizedDatabaseName, cancellationToken).ConfigureAwait(false);
            if (!exists && !access.CanAddDatabase)
            {
                throw new UnauthorizedAccessException($"ApiKey is not allowed to create database '{normalizedDatabaseName}'.");
            }

            LogicalDatabaseRegistration registration = await _logicalDatabaseCatalog.GetOrCreateAsync(normalizedDatabaseName, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Database request context resolved. Database={Database}", registration.DatabaseName);

            return new DatabaseRequestContext
            {
                DatabaseName = registration.DatabaseName,
                ApiKey = access.ApiKey,
                IsRoot = access.IsRoot,
                CanAddDatabase = access.CanAddDatabase,
                CanDeleteDatabase = access.CanDeleteDatabase,
                CanReadDocument = access.CanReadDocument,
                CanWriteDocument = access.CanWriteDocument,
                CanUpdateDocument = access.CanUpdateDocument,
                CanDeleteDocument = access.CanDeleteDocument
            };
        }
    }
}
