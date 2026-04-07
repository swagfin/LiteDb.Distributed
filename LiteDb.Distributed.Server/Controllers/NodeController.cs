using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("node")]
public class NodeController : ControllerBase
{
    private readonly ClusterNodeOptions _nodeOptions;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;
    private readonly ILogger<NodeController> _logger;

    public NodeController(ClusterNodeOptions nodeOptions, ILogicalDatabaseCatalog logicalDatabaseCatalog, ILogger<NodeController> logger)
    {
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> GetNodeInfoAsync(CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<LogicalDatabaseRegistration> databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        _logger.LogDebug("Node info requested. NodeId={NodeId} LogicalDatabaseCount={LogicalDatabaseCount} DurationMs={DurationMs}", _nodeOptions.NodeId, databases.Count, stopwatch.Elapsed.TotalMilliseconds);

        return Ok(new
        {
            NodeId = _nodeOptions.NodeId,
            TimestampUtc = DateTime.UtcNow,
            LogicalDatabaseCount = databases.Count
        });
    }
}

