using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LiteDb.Distributed.Infrastructure.Replication;

public sealed class ClusterReplicationBackgroundService : BackgroundService
{
    private readonly IClusterReplicationService _clusterReplicationService;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
    private readonly IDatabaseContextAccessor _databaseContextAccessor;
    private readonly ILogger<ClusterReplicationBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public ClusterReplicationBackgroundService(
        ClusterNodeOptions options,
        IClusterReplicationService clusterReplicationService,
        ILogicalDatabaseCatalog logicalDatabaseCatalog,
        IDatabaseContextAccessor databaseContextAccessor,
        ILogger<ClusterReplicationBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clusterReplicationService = clusterReplicationService ?? throw new ArgumentNullException(nameof(clusterReplicationService));
        _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
        _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var seconds = Math.Max(1, options.ReplicationIntervalSeconds);
        _interval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cluster replication worker started. Interval={IntervalSeconds}s", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var iterationStopwatch = Stopwatch.StartNew();

            try
            {
                var databases = await _logicalDatabaseCatalog.GetAllAsync(stoppingToken).ConfigureAwait(false);

                _logger.LogDebug(
                    "Cluster replication iteration started. DatabaseCount={DatabaseCount}",
                    databases.Count);

                foreach (var database in databases)
                {
                    var databaseStopwatch = Stopwatch.StartNew();

                    using var scope = _databaseContextAccessor.BeginScope(new DatabaseRequestContext
                    {
                        DatabaseName = database.DatabaseName,
                        Credential = database.Credential
                    });

                    await _clusterReplicationService.ReplicateOnceAsync(stoppingToken).ConfigureAwait(false);
                    databaseStopwatch.Stop();

                    _logger.LogDebug(
                        "Database replication iteration completed. Database={Database} DurationMs={DurationMs}",
                        database.DatabaseName,
                        databaseStopwatch.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster replication iteration failed.");
            }
            finally
            {
                iterationStopwatch.Stop();
                _logger.LogDebug(
                    "Cluster replication iteration finished. DurationMs={DurationMs}",
                    iterationStopwatch.Elapsed.TotalMilliseconds);
            }

            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }
}

