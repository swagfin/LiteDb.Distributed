using System.Net.WebSockets;

namespace LiteDb.Distributed.Server.Infrastructure.Replication
{
    public interface IReplicationWebSocketHandler
    {
        Task HandleConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken = default);
    }

}
