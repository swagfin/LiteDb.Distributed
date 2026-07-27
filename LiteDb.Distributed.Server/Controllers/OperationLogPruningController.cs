using LiteDb.Distributed.Server.Filters;
using LiteDb.Distributed.Server.Replication;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireNodeReplicationApiKey]
    [Route("api/replication/operation-log")]
    public class OperationLogPruningController : ControllerBase
    {
        private readonly IOperationLogPruningService _operationLogPruningService;

        public OperationLogPruningController(IOperationLogPruningService operationLogPruningService)
        {
            _operationLogPruningService = operationLogPruningService ?? throw new ArgumentNullException(nameof(operationLogPruningService));
        }

        [HttpPost("prune")]
        public async Task<IActionResult> PruneAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<OperationLogPruningDatabaseResult> results = await _operationLogPruningService.PruneOnceAsync(cancellationToken).ConfigureAwait(false);
            return Ok(results);
        }
    }
}
