using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;
using LiteDb.Distributed.Server.Data;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public class ReplicationOrchestrator : IReplicationOrchestrator
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IClusterReplicationService _clusterReplicationService;
        private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
        private readonly IDatabaseContextAccessor _databaseContextAccessor;
        private readonly ILogger<ReplicationOrchestrator> _logger;
        // Prevent overlapping replication runs that could race on the same logical databases.
        private readonly SemaphoreSlim _replicationGate = new(1, 1);

        public ReplicationOrchestrator(ClusterNodeOptions nodeOptions, IClusterReplicationService clusterReplicationService, ILogicalDatabaseCatalog logicalDatabaseCatalog, IDatabaseContextAccessor databaseContextAccessor, ILogger<ReplicationOrchestrator> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _clusterReplicationService = clusterReplicationService ?? throw new ArgumentNullException(nameof(clusterReplicationService));
            _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
            _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ReplicateAllDatabasesAsync(string reason, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Replication reason is required.", nameof(reason));
            }

            await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Replicate each registered logical database in isolation via scoped request context.
                Stopwatch totalStopwatch = Stopwatch.StartNew();
                IReadOnlyList<LogicalDatabaseRegistration> databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Cluster replication batch started. Reason={Reason} DatabaseCount={DatabaseCount}", reason, databases.Count);

                foreach (LogicalDatabaseRegistration database in databases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReplicateDatabaseCoreAsync(database.DatabaseName, _nodeOptions.ReplicationApiKey, reason, suppressExceptions: true, cancellationToken).ConfigureAwait(false);
                }

                totalStopwatch.Stop();
                _logger.LogDebug("Cluster replication batch completed. Reason={Reason} DatabaseCount={DatabaseCount} DurationMs={DurationMs}", reason, databases.Count, totalStopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                _replicationGate.Release();
            }
        }

        public async Task ReplicateDatabaseAsync(string databaseName, string apiKey, string reason, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Replication reason is required.", nameof(reason));
            }

            string normalizedDatabase = DatabaseNameNormalizer.Normalize(databaseName);

            await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ReplicateDatabaseCoreAsync(normalizedDatabase, apiKey, reason, suppressExceptions: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _replicationGate.Release();
            }
        }

        private async Task ReplicateDatabaseCoreAsync(string databaseName, string apiKey, string reason, bool suppressExceptions, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                using IDisposable scope = _databaseContextAccessor.BeginScope(new DatabaseRequestContext
                {
                    DatabaseName = databaseName,
                    ApiKey = string.IsNullOrWhiteSpace(apiKey) ? _nodeOptions.ReplicationApiKey : apiKey,
                    IsRoot = true,
                    CanAddDatabase = true,
                    CanDeleteDatabase = true,
                    CanReadDocument = true,
                    CanWriteDocument = true,
                    CanUpdateDocument = true,
                    CanDeleteDocument = true
                });

                _logger.LogDebug("Database replication started. Reason={Reason} Database={Database}", reason, databaseName);

                await _clusterReplicationService.ReplicateOnceAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogDebug("Database replication completed. Reason={Reason} Database={Database} DurationMs={DurationMs}", reason, databaseName, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Database replication failed. Reason={Reason} Database={Database} DurationMs={DurationMs}", reason, databaseName, stopwatch.Elapsed.TotalMilliseconds);

                if (!suppressExceptions)
                {
                    throw;
                }
            }
        }
    }
}
