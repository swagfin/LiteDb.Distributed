using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Replication
{
    public class ClusterReplicationBackgroundService : BackgroundService
    {
        private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(1);
        private readonly IReplicationOrchestrator _replicationOrchestrator;
        private readonly ILogger<ClusterReplicationBackgroundService> _logger;
        private readonly TimeSpan _interval;

        public ClusterReplicationBackgroundService(IReplicationOrchestrator replicationOrchestrator, ILogger<ClusterReplicationBackgroundService> logger)
        {
            _replicationOrchestrator = replicationOrchestrator ?? throw new ArgumentNullException(nameof(replicationOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interval = CatchUpInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Cluster replication worker started. IntervalMs={IntervalMs}", _interval.TotalMilliseconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                Stopwatch iterationStopwatch = Stopwatch.StartNew();

                try
                {
                    await _replicationOrchestrator.ReplicateAllDatabasesAsync("safety-sweep", stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cluster replication iteration failed.");
                }
                finally
                {
                    iterationStopwatch.Stop();
                    _logger.LogDebug("Cluster replication iteration finished. DurationMs={DurationMs}", iterationStopwatch.Elapsed.TotalMilliseconds);
                }

                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

}
