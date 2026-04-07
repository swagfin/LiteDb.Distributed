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
        private readonly ILogger<DatabaseRequestContextResolver> _logger;

        public DatabaseRequestContextResolver(ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogger<DatabaseRequestContextResolver> logger)
        {
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<DatabaseRequestContext> ResolveAsync(IHeaderDictionary headers, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(headers);

            string rawDatabaseName = headers[DatabaseHeader].ToString();
            string normalizedDatabaseName = DatabaseNameNormalizer.Normalize(rawDatabaseName);
            string apiKey = headers[ApiKeyHeader].ToString();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("ApiKey header is required.");
            }
            LogicalDatabaseRegistration registration = await _logicalDatabaseCatalog.GetOrCreateAsync(normalizedDatabaseName, apiKey, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Database request context resolved. Database={Database}", registration.DatabaseName);

            return new DatabaseRequestContext
            {
                DatabaseName = registration.DatabaseName,
                Credential = registration.Credential
            };
        }
    }

}
