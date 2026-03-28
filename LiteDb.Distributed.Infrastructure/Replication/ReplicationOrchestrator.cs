using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LiteDb.Distributed.Infrastructure.Replication;

public sealed class ReplicationOrchestrator : IReplicationOrchestrator
{
    private readonly IClusterReplicationService _clusterReplicationService;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
    private readonly IDatabaseContextAccessor _databaseContextAccessor;
    private readonly ILogger<ReplicationOrchestrator> _logger;
    private readonly SemaphoreSlim _replicationGate = new(1, 1);

    public ReplicationOrchestrator(IClusterReplicationService clusterReplicationService, ILogicalDatabaseCatalog logicalDatabaseCatalog, IDatabaseContextAccessor databaseContextAccessor, ILogger<ReplicationOrchestrator> logger)
    {
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
            var totalStopwatch = Stopwatch.StartNew();
            var databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Cluster replication batch started. Reason={Reason} DatabaseCount={DatabaseCount}", reason, databases.Count);

            foreach (var database in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReplicateDatabaseCoreAsync(database, reason, suppressExceptions: true, cancellationToken).ConfigureAwait(false);
            }

            totalStopwatch.Stop();
            _logger.LogDebug("Cluster replication batch completed. Reason={Reason} DatabaseCount={DatabaseCount} DurationMs={DurationMs}", reason, databases.Count, totalStopwatch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _replicationGate.Release();
        }
    }

    public async Task ReplicateDatabaseAsync(string databaseName, string credential, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Replication reason is required.", nameof(reason));
        }

        var registration = await _logicalDatabaseCatalog.GetOrCreateAsync(databaseName, credential, cancellationToken).ConfigureAwait(false);

        await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplicateDatabaseCoreAsync(registration, reason, suppressExceptions: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _replicationGate.Release();
        }
    }

    private async Task ReplicateDatabaseCoreAsync(LogicalDatabaseRegistration database, string reason, bool suppressExceptions, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var scope = _databaseContextAccessor.BeginScope(new DatabaseRequestContext
            {
                DatabaseName = database.DatabaseName,
                Credential = database.Credential
            });

            _logger.LogDebug("Database replication started. Reason={Reason} Database={Database}", reason, database.DatabaseName);

            await _clusterReplicationService.ReplicateOnceAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogDebug("Database replication completed. Reason={Reason} Database={Database} DurationMs={DurationMs}", reason, database.DatabaseName, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Database replication failed. Reason={Reason} Database={Database} DurationMs={DurationMs}", reason, database.DatabaseName, stopwatch.Elapsed.TotalMilliseconds);

            if (!suppressExceptions)
            {
                throw;
            }
        }
    }
}
