using DistributedLiteDb.Core.Abstractions;
using DistributedLiteDb.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DistributedLiteDb.Infrastructure.Replication;

public sealed class ClusterReplicationBackgroundService : BackgroundService
{
    private readonly IClusterReplicationService _clusterReplicationService;
    private readonly ILogger<ClusterReplicationBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public ClusterReplicationBackgroundService(
        ClusterNodeOptions options,
        IClusterReplicationService clusterReplicationService,
        ILogger<ClusterReplicationBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clusterReplicationService = clusterReplicationService ?? throw new ArgumentNullException(nameof(clusterReplicationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var seconds = Math.Max(1, options.ReplicationIntervalSeconds);
        _interval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cluster replication worker started. Interval={IntervalSeconds}s", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _clusterReplicationService.ReplicateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster replication iteration failed.");
            }

            await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
