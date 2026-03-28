using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("api/replication")]
public sealed class ReplicationController : ControllerBase
{
    private readonly ClusterNodeOptions _nodeOptions;
    private readonly IOperationIngestionService _ingestionService;
    private readonly IOperationLogStore _operationLogStore;
    private readonly IClusterReplicationService _clusterReplicationService;

    public ReplicationController(
        ClusterNodeOptions nodeOptions,
        IOperationIngestionService ingestionService,
        IOperationLogStore operationLogStore,
        IClusterReplicationService clusterReplicationService)
    {
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _operationLogStore = operationLogStore ?? throw new ArgumentNullException(nameof(operationLogStore));
        _clusterReplicationService = clusterReplicationService ?? throw new ArgumentNullException(nameof(clusterReplicationService));
    }

    [HttpPost("push")]
    public async Task<IActionResult> PushAsync([FromBody] ReplicationPushRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _ingestionService
                .IngestAsync(_nodeOptions.NodeId, request.Operations, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new ReplicationPushResponse
            {
                AcceptedCount = result.AcceptedCount
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPost("pull")]
    public async Task<IActionResult> PullAsync([FromBody] ReplicationPullRequest request, CancellationToken cancellationToken)
    {
        if (request.BatchSize <= 0)
        {
            return BadRequest(new { Error = "BatchSize must be greater than zero." });
        }

        var operations = await _operationLogStore.GetOperationsAfterLogSequenceAsync(request.AfterLogSequence, request.BatchSize, cancellationToken).ConfigureAwait(false);

        return Ok(new ReplicationPullResponse
        {
            Operations = operations
        });
    }

    [HttpPost("trigger")]
    public async Task<IActionResult> TriggerAsync(CancellationToken cancellationToken)
    {
        await _clusterReplicationService.ReplicateOnceAsync(cancellationToken).ConfigureAwait(false);
        return Accepted();
    }
}
