using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;

namespace LiteDb.Distributed.Infrastructure.Context;

public sealed class DatabaseRequestContextResolver : IDatabaseRequestContextResolver
{
    private const string DatabaseHeader = "Database";
    private const string PasswordHeader = "Password";
    private const string ApiKeyHeader = "ApiKey";

    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;

    public DatabaseRequestContextResolver(ILogicalDatabaseCatalog logicalDatabaseCatalog)
    {
        _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
    }

    public async Task<DatabaseRequestContext> ResolveAsync(
        IHeaderDictionary headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var rawDatabaseName = headers[DatabaseHeader].ToString();
        var normalizedDatabaseName = DatabaseNameNormalizer.Normalize(rawDatabaseName);

        var password = headers[PasswordHeader].ToString();
        var apiKey = headers[ApiKeyHeader].ToString();

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

        var credential = string.IsNullOrWhiteSpace(password) ? apiKey : password;

        var registration = await _logicalDatabaseCatalog
            .GetOrCreateAsync(normalizedDatabaseName, credential, cancellationToken)
            .ConfigureAwait(false);

        return new DatabaseRequestContext
        {
            DatabaseName = registration.DatabaseName,
            Credential = registration.Credential
        };
    }
}
