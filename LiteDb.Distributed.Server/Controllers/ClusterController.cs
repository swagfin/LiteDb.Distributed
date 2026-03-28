using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("api/cluster")]
public sealed class ClusterController : ControllerBase
{
    private readonly ClusterNodeOptions _nodeOptions;
    private readonly IClusterPeerRegistry _peerRegistry;

    public ClusterController(ClusterNodeOptions nodeOptions, IClusterPeerRegistry peerRegistry)
    {
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));
    }

    [HttpPost("peers")]
    public async Task<IActionResult> UpsertPeerAsync([FromBody] ClusterPeer peer, CancellationToken cancellationToken)
    {
        if (string.Equals(peer.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal))
        {
            return BadRequest(new { Error = "Cannot register local node as a peer." });
        }

        await _peerRegistry.UpsertPeerAsync(peer with { UpdatedUtc = DateTime.UtcNow }, cancellationToken).ConfigureAwait(false);
        return Ok();
    }

    [HttpGet("peers")]
    public async Task<IActionResult> GetPeersAsync(CancellationToken cancellationToken)
    {
        var peers = await _peerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);
        return Ok(peers);
    }
}
