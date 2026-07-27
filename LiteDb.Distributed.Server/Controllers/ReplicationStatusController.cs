using LiteDb.Distributed.Server.Filters;
using LiteDb.Distributed.Server.Replication;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireNodeReplicationApiKey]
    [Route("api/replication/status")]
    public class ReplicationStatusController : ControllerBase
    {
        private readonly IReplicationStatusService _replicationStatusService;

        public ReplicationStatusController(IReplicationStatusService replicationStatusService)
        {
            _replicationStatusService = replicationStatusService ?? throw new ArgumentNullException(nameof(replicationStatusService));
        }

        [HttpGet]
        public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
        {
            ReplicationStatusSnapshot status = await _replicationStatusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Ok(status);
        }
    }
}
