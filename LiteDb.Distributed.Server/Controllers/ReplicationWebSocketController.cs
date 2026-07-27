using LiteDb.Distributed.Server.Replication;
using LiteDb.Distributed.Server.Filters;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireNodeReplicationApiKey]
    [Route("ws/replication")]
    public class ReplicationWebSocketController : ControllerBase
    {
        private readonly IReplicationWebSocketHandler _webSocketHandler;
        private readonly ILogger<ReplicationWebSocketController> _logger;

        public ReplicationWebSocketController(IReplicationWebSocketHandler webSocketHandler, ILogger<ReplicationWebSocketController> logger)
        {
            _webSocketHandler = webSocketHandler ?? throw new ArgumentNullException(nameof(webSocketHandler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        public async Task GetAsync(CancellationToken cancellationToken)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await HttpContext.Response.WriteAsJsonAsync(new { Error = "A WebSocket request is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogDebug("Replication websocket upgrade requested. Remote={Remote}", HttpContext.Connection.RemoteIpAddress?.ToString());

            using System.Net.WebSockets.WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await _webSocketHandler.HandleConnectionAsync(webSocket, cancellationToken).ConfigureAwait(false);
        }
    }

}
