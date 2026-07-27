using System.Collections.Concurrent;
using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Server.Data
{
    public class LogicalDatabaseStoreProvider : ILogicalDatabaseStoreProvider
    {
        private readonly ClusterNodeOptions _options;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly IDatabaseContextAccessor _databaseContextAccessor;
        private readonly ILogger<LogicalDatabaseStoreProvider> _logger;
        // Lazy factory avoids opening every database file upfront and keeps one store per logical database.
        private readonly ConcurrentDictionary<string, Lazy<LiteDbNodeStore>> _stores = new(StringComparer.Ordinal);

        private bool _disposed;

        public LogicalDatabaseStoreProvider(ClusterNodeOptions options, ILogicalDatabaseCatalog logicalDatabaseCatalog, IDatabaseContextAccessor databaseContextAccessor, ILogger<LogicalDatabaseStoreProvider> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<LiteDbNodeStore> GetCurrentStoreAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            DatabaseRequestContext? context = _databaseContextAccessor.Current;
            if (context is null)
            {
                throw new InvalidOperationException("No active logical database context for the current execution flow.");
            }

            return await GetStoreAsync(context.DatabaseName, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LiteDbNodeStore> GetStoreAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            LogicalDatabaseRegistration registration = await _logicalDatabaseCatalog.GetOrCreateAsync(databaseName, cancellationToken).ConfigureAwait(false);
            Lazy<LiteDbNodeStore> lazyStore = _stores.GetOrAdd(
                registration.DatabaseName,
                _ => new Lazy<LiteDbNodeStore>(() => CreateStore(registration), LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return lazyStore.Value;
            }
            catch (Exception ex)
            {
                // If creation failed, remove this lazy entry so the next request can retry.
                _stores.TryRemove(new KeyValuePair<string, Lazy<LiteDbNodeStore>>(registration.DatabaseName, lazyStore));
                _logger.LogError(ex, "Failed opening logical database store after retry-safe lazy initialization. NodeId={NodeId} Database={Database}", _options.NodeId, registration.DatabaseName);
                throw;
            }
        }

        private LiteDbNodeStore CreateStore(LogicalDatabaseRegistration registration)
        {
            string rootDataDirectory = ResolveDataDirectory(_options.DataDirectory);
            string nodeDataDirectory = Path.Combine(rootDataDirectory, _options.NodeId);
            Directory.CreateDirectory(nodeDataDirectory);
            string databasePath = Path.Combine(nodeDataDirectory, $"{registration.DatabaseName}.db");

            _logger.LogInformation("Opening logical database store. NodeId={NodeId} Database={Database} DatabasePath={DatabasePath}", _options.NodeId, registration.DatabaseName, databasePath);

            try
            {
                return new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = _options.NodeId,
                    DatabaseName = registration.DatabaseName,
                    DatabasePath = databasePath,
                    SeedPeers = _options.SeedPeers
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed opening logical database store. NodeId={NodeId} Database={Database} DatabasePath={DatabasePath}", _options.NodeId, registration.DatabaseName, databasePath);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (Lazy<LiteDbNodeStore> lazyStore in _stores.Values)
            {
                if (!lazyStore.IsValueCreated)
                {
                    continue;
                }

                lazyStore.Value.Dispose();
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

        private static string ResolveDataDirectory(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            }

            return Path.IsPathRooted(dataDirectory) ? Path.GetFullPath(dataDirectory) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dataDirectory));
        }
    }
}
