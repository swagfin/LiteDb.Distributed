using System.Collections.Concurrent;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;

namespace LiteDb.Distributed.Infrastructure.Storage;

public sealed class LogicalDatabaseStoreProvider : ILogicalDatabaseStoreProvider
{
    private readonly ClusterNodeOptions _options;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
    private readonly IDatabaseContextAccessor _databaseContextAccessor;
    private readonly ConcurrentDictionary<string, LiteDbNodeStore> _stores = new(StringComparer.Ordinal);

    private bool _disposed;

    public LogicalDatabaseStoreProvider(
        ClusterNodeOptions options,
        ILogicalDatabaseCatalog logicalDatabaseCatalog,
        IDatabaseContextAccessor databaseContextAccessor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
        _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
    }

    public async Task<LiteDbNodeStore> GetCurrentStoreAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var context = _databaseContextAccessor.Current;
        if (context is null)
        {
            throw new InvalidOperationException("No active logical database context for the current execution flow.");
        }

        return await GetStoreAsync(context.DatabaseName, context.Credential, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiteDbNodeStore> GetStoreAsync(
        string databaseName,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var registration = await _logicalDatabaseCatalog
            .GetOrCreateAsync(databaseName, credential, cancellationToken)
            .ConfigureAwait(false);

        return _stores.GetOrAdd(
            registration.DatabaseName,
            _ =>
            {
                var dataDirectory = Path.GetFullPath(_options.DataDirectory);
                Directory.CreateDirectory(dataDirectory);

                return new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = _options.NodeId,
                    DatabaseName = registration.DatabaseName,
                    DatabasePassword = registration.Credential,
                    BusinessDatabasePath = Path.Combine(dataDirectory, $"{registration.DatabaseName}.db"),
                    MetadataDatabasePath = Path.Combine(dataDirectory, $"{registration.DatabaseName}.db.metadata"),
                    SeedPeers = _options.SeedPeers
                });
            });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var store in _stores.Values)
        {
            store.Dispose();
        }

        _stores.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LogicalDatabaseStoreProvider));
        }
    }
}
