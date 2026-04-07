using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("api/cluster")]
    public class ClusterController : ControllerBase
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IClusterPeerRegistry _peerRegistry;
        private readonly ILogger<ClusterController> _logger;

        public ClusterController(ClusterNodeOptions nodeOptions, IClusterPeerRegistry peerRegistry, ILogger<ClusterController> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost("peers")]
        public async Task<IActionResult> UpsertPeerAsync([FromBody] ClusterPeer peer, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (string.Equals(peer.NodeId, _nodeOptions.NodeId, StringComparison.Ordinal))
            {
                stopwatch.Stop();
                _logger.LogWarning("Rejected peer registration for local node id. NodeId={NodeId} DurationMs={DurationMs}", _nodeOptions.NodeId, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = "Cannot register local node as a peer." });
            }

            await _peerRegistry.UpsertPeerAsync(peer with { UpdatedUtc = DateTime.UtcNow }, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation("Peer upserted. NodeId={NodeId} PeerNodeId={PeerNodeId} BaseUrl={BaseUrl} DurationMs={DurationMs}", _nodeOptions.NodeId, peer.NodeId, peer.BaseUrl, stopwatch.Elapsed.TotalMilliseconds);

            return Ok();
        }

        [HttpGet("peers")]
        public async Task<IActionResult> GetPeersAsync(CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<ClusterPeer> peers = await _peerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogDebug("Peer list requested. NodeId={NodeId} PeerCount={PeerCount} DurationMs={DurationMs}", _nodeOptions.NodeId, peers.Count, stopwatch.Elapsed.TotalMilliseconds);

            return Ok(peers);
        }
    }

}
