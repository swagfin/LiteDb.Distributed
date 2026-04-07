using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Infrastructure.Context;

public class DatabaseRequestContextResolver : IDatabaseRequestContextResolver
{
    private const string DatabaseHeader = "Database";
    private const string PasswordHeader = "Password";
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

        string password = headers[PasswordHeader].ToString();
        string apiKey = headers[ApiKeyHeader].ToString();

        if (string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Either Password or ApiKey header is required.");
        }

        if (!string.IsNullOrWhiteSpace(password)
            && !string.IsNullOrWhiteSpace(apiKey)
            && !string.Equals(password, apiKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Password and ApiKey headers both provided but do not match.");
        }

        string credential = string.IsNullOrWhiteSpace(password) ? apiKey : password;

        LogicalDatabaseRegistration registration = await _logicalDatabaseCatalog.GetOrCreateAsync(normalizedDatabaseName, credential, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Database request context resolved. Database={Database}", registration.DatabaseName);

        return new DatabaseRequestContext
        {
            DatabaseName = registration.DatabaseName,
            Credential = registration.Credential
        };
    }
}


