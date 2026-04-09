using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Common;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Replication;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteDb.Distributed.Infrastructure.Cache
{
    public class CacheExpirationBackgroundService : BackgroundService
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly IDatabaseContextAccessor _databaseContextAccessor;
        private readonly ILocalDocumentReader _reader;
        private readonly ILocalDocumentWriter _writer;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<CacheExpirationBackgroundService> _logger;

        public CacheExpirationBackgroundService(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, IDatabaseContextAccessor databaseContextAccessor, ILocalDocumentReader reader, ILocalDocumentWriter writer, IReplicationSignalPublisher replicationSignalPublisher, ILogger<CacheExpirationBackgroundService> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int cleanupIntervalSeconds = Math.Max(1, _nodeOptions.CacheCleanupIntervalSeconds);
            TimeSpan cleanupInterval = TimeSpan.FromSeconds(cleanupIntervalSeconds);
            _logger.LogInformation("Cache expiration sweeper started. NodeId={NodeId} IntervalSeconds={IntervalSeconds} BatchSize={BatchSize} MaxScanPages={MaxScanPages}", _nodeOptions.NodeId, cleanupIntervalSeconds, Math.Max(1, _nodeOptions.CacheCleanupBatchSize), Math.Max(1, _nodeOptions.CacheCleanupMaxScanPages));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAllDatabasesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cache expiration sweep failed unexpectedly. NodeId={NodeId}", _nodeOptions.NodeId);
                }

                try
                {
                    await Task.Delay(cleanupInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task SweepAllDatabasesAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<LogicalDatabaseRegistration> registrations = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            if (registrations.Count == 0)
            {
                return;
            }

            foreach (LogicalDatabaseRegistration registration in registrations.OrderBy(x => x.DatabaseName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using IDisposable scope = _databaseContextAccessor.BeginScope(new DatabaseRequestContext
                {
                    DatabaseName = registration.DatabaseName,
                    ApiKey = "root",
                    IsRoot = true,
                    CanAddDatabase = true,
                    CanDeleteDatabase = true,
                    CanReadDocument = true,
                    CanWriteDocument = true,
                    CanUpdateDocument = true,
                    CanDeleteDocument = true
                });

                int deleted = await SweepDatabaseAsync(registration.DatabaseName, cancellationToken).ConfigureAwait(false);
                if (deleted <= 0)
                {
                    continue;
                }

                _replicationSignalPublisher.NotifyLocalChange("cache-expiration-sweep");
                _logger.LogDebug("Cache expiration sweep deleted entries. NodeId={NodeId} Database={Database} Deleted={Deleted}", _nodeOptions.NodeId, registration.DatabaseName, deleted);
            }
        }

        private async Task<int> SweepDatabaseAsync(string databaseName, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            IReadOnlyList<string> expiredKeys = await CollectExpiredKeysAsync(now, cancellationToken).ConfigureAwait(false);
            if (expiredKeys.Count == 0)
            {
                return 0;
            }

            int deletedCount = 0;
            foreach (string key in expiredKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _writer.DeleteAsync(Common.CacheCollectionName, key, cancellationToken: cancellationToken).ConfigureAwait(false);
                    deletedCount++;
                }
                catch (VersionMismatchException ex)
                {
                    _logger.LogDebug(ex, "Cache expiration sweep skipped key due to version change. NodeId={NodeId} Database={Database} Key={Key}", _nodeOptions.NodeId, databaseName, key);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogDebug(ex, "Cache expiration sweep skipped invalid key. NodeId={NodeId} Database={Database} Key={Key}", _nodeOptions.NodeId, databaseName, key);
                }
            }

            return deletedCount;
        }

        private async Task<IReadOnlyList<string>> CollectExpiredKeysAsync(DateTime utcNow, CancellationToken cancellationToken)
        {
            int maxScanPages = Math.Max(1, _nodeOptions.CacheCleanupMaxScanPages);
            int batchSize = Math.Max(1, _nodeOptions.CacheCleanupBatchSize);
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            int skip = 0;

            for (int page = 0; page < maxScanPages; page++)
            {
                IReadOnlyList<CacheSweepEntry> entries = await _reader.ListAsync<CacheSweepEntry>(Common.CacheCollectionName, skip, batchSize, cancellationToken).ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    break;
                }

                foreach (CacheSweepEntry entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id))
                    {
                        continue;
                    }

                    if (NormalizeUtc(entry.ExpiresAtUtc) <= utcNow)
                    {
                        keys.Add(entry.Id);
                        if (keys.Count >= batchSize)
                        {
                            return keys.ToList();
                        }
                    }
                }

                if (entries.Count < batchSize)
                {
                    break;
                }

                skip += batchSize;
            }

            return keys.ToList();
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private class CacheSweepEntry
        {
            public string Id { get; set; } = string.Empty;
            public DateTime ExpiresAtUtc { get; set; }
        }
    }
}
