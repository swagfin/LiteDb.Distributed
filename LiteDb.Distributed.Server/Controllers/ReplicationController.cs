using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Server.Filters;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireNodeReplicationApiKey]
    [ResolveNodeReplicationDatabase]
    [Route("api/replication")]
    public class ReplicationController : ControllerBase
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IOperationIngestionService _ingestionService;
        private readonly IOperationLogStore _operationLogStore;
        private readonly ILogger<ReplicationController> _logger;

        public ReplicationController(ClusterNodeOptions nodeOptions, IOperationIngestionService ingestionService, IOperationLogStore operationLogStore, ILogger<ReplicationController> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
            _operationLogStore = operationLogStore ?? throw new ArgumentNullException(nameof(operationLogStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost("push")]
        public async Task<IActionResult> PushAsync([FromBody] ReplicationPushRequest request, CancellationToken cancellationToken)
        {
            int operationCount = request.Operations?.Count ?? 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            _logger.LogDebug("Replication push received. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} OperationCount={OperationCount}", _nodeOptions.NodeId, request.SourceNodeId, operationCount);

            if (request.Operations is null)
            {
                stopwatch.Stop();

                _logger.LogWarning("Replication push rejected because operations payload was null. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} DurationMs={DurationMs}", _nodeOptions.NodeId, request.SourceNodeId, stopwatch.Elapsed.TotalMilliseconds);

                return BadRequest(new { Error = "Operations payload is required." });
            }

            try
            {
                OperationIngestionResult result = await _ingestionService.IngestAsync(_nodeOptions.NodeId, request.Operations, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                int notAppliedCount = Math.Max(0, operationCount - result.AcceptedCount);

                _logger.LogInformation("Replication push applied. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} Received={Received} Accepted={Accepted} Conflicts={Conflicts} NotApplied={NotApplied} ApplyDurationMs={ApplyDurationMs}", _nodeOptions.NodeId, request.SourceNodeId, operationCount, result.AcceptedCount, result.ConflictCount, notAppliedCount, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(new ReplicationPushResponse
                {
                    AcceptedCount = result.AcceptedCount
                });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();

                _logger.LogWarning(ex, "Replication push rejected. LocalNodeId={LocalNodeId} SourceNodeId={SourceNodeId} OperationCount={OperationCount} DurationMs={DurationMs}", _nodeOptions.NodeId, request.SourceNodeId, operationCount, stopwatch.Elapsed.TotalMilliseconds);

                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpPost("pull")]
        public async Task<IActionResult> PullAsync([FromBody] ReplicationPullRequest request, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (request.BatchSize <= 0)
            {
                stopwatch.Stop();
                _logger.LogWarning("Replication pull rejected due to invalid batch size. LocalNodeId={LocalNodeId} RequestingNodeId={RequestingNodeId} BatchSize={BatchSize} DurationMs={DurationMs}", _nodeOptions.NodeId, request.RequestingNodeId, request.BatchSize, stopwatch.Elapsed.TotalMilliseconds);

                return BadRequest(new { Error = "BatchSize must be greater than zero." });
            }

            _logger.LogDebug("Replication pull received. LocalNodeId={LocalNodeId} RequestingNodeId={RequestingNodeId} AfterLogSequence={AfterLogSequence} BatchSize={BatchSize}", _nodeOptions.NodeId, request.RequestingNodeId, request.AfterLogSequence, request.BatchSize);

            IReadOnlyList<OperationRecord> operations = await _operationLogStore.GetOperationsAfterLogSequenceAsync(request.AfterLogSequence, request.BatchSize, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogDebug("Replication pull served. LocalNodeId={LocalNodeId} RequestingNodeId={RequestingNodeId} ReturnedCount={ReturnedCount} DurationMs={DurationMs}", _nodeOptions.NodeId, request.RequestingNodeId, operations.Count, stopwatch.Elapsed.TotalMilliseconds);

            return Ok(new ReplicationPullResponse
            {
                Operations = operations
            });
        }
    }
}
