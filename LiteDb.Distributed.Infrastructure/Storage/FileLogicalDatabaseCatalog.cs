using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;

namespace LiteDb.Distributed.Infrastructure.Storage;

public sealed class FileLogicalDatabaseCatalog : ILogicalDatabaseCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _catalogPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogicalDatabaseCatalog(ClusterNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(dataDirectory);

        _catalogPath = Path.Combine(dataDirectory, "_logical_databases.catalog.json");
    }

    public async Task<LogicalDatabaseRegistration> GetOrCreateAsync(
        string databaseName,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = DatabaseNameNormalizer.Normalize(databaseName);
        var normalizedCredential = NormalizeCredential(credential);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadInternalAsync(cancellationToken).ConfigureAwait(false);

            if (catalog.TryGetValue(normalizedName, out var existing))
            {
                if (!SecureEquals(existing.Credential, normalizedCredential))
                {
                    throw new DatabaseAuthenticationException(
                        $"Credential is invalid for database '{normalizedName}'.");
                }

                return ToRegistration(existing);
            }

            var now = DateTime.UtcNow;
            var entry = new CatalogEntry
            {
                DatabaseName = normalizedName,
                Credential = normalizedCredential,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            // TODO: Move credentials to encrypted at-rest storage or a dedicated secret provider.
            catalog[normalizedName] = entry;
            await WriteInternalAsync(catalog, cancellationToken).ConfigureAwait(false);

            return ToRegistration(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LogicalDatabaseRegistration>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await ReadInternalAsync(cancellationToken).ConfigureAwait(false);
            return catalog.Values
                .OrderBy(x => x.DatabaseName, StringComparer.Ordinal)
                .Select(ToRegistration)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, CatalogEntry>> ReadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(_catalogPath);
        var wrapper = await JsonSerializer
            .DeserializeAsync<CatalogWrapper>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var entries = wrapper?.Databases ?? new List<CatalogEntry>();
        return entries.ToDictionary(x => x.DatabaseName, x => x, StringComparer.Ordinal);
    }

    private async Task WriteInternalAsync(
        Dictionary<string, CatalogEntry> catalog,
        CancellationToken cancellationToken)
    {
        var wrapper = new CatalogWrapper
        {
            Databases = catalog.Values
                .OrderBy(x => x.DatabaseName, StringComparer.Ordinal)
                .ToList()
        };

        var directory = Path.GetDirectoryName(_catalogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_catalogPath);
        await JsonSerializer.SerializeAsync(stream, wrapper, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeCredential(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("Password or ApiKey header is required.", nameof(credential));
        }

        return credential.Trim();
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static LogicalDatabaseRegistration ToRegistration(CatalogEntry entry)
    {
        return new LogicalDatabaseRegistration
        {
            DatabaseName = entry.DatabaseName,
            Credential = entry.Credential,
            CreatedUtc = entry.CreatedUtc,
            UpdatedUtc = entry.UpdatedUtc
        };
    }

    private sealed class CatalogWrapper
    {
        public List<CatalogEntry> Databases { get; init; } = new();
    }

    private sealed class CatalogEntry
    {
        public string DatabaseName { get; init; } = string.Empty;
        public string Credential { get; init; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
