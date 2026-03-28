using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("")]
public sealed class NodeController : ControllerBase
{
    private readonly ClusterNodeOptions _nodeOptions;
    private readonly ILogicalDatabaseCatalog _logicalDatabaseCatalog;

    public NodeController(
        ClusterNodeOptions nodeOptions,
        ILogicalDatabaseCatalog logicalDatabaseCatalog)
    {
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _logicalDatabaseCatalog = logicalDatabaseCatalog ?? throw new ArgumentNullException(nameof(logicalDatabaseCatalog));
    }

    [HttpGet]
    public async Task<IActionResult> GetNodeInfoAsync(CancellationToken cancellationToken)
    {
        var databases = await _logicalDatabaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            NodeId = _nodeOptions.NodeId,
            TimestampUtc = DateTime.UtcNow,
            LogicalDatabaseCount = databases.Count
        });
    }
}
