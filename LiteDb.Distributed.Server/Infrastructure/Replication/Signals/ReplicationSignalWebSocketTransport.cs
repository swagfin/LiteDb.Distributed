using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Infrastructure.Replication.Signals
{
    internal static class ReplicationSignalWebSocketTransport
    {
        private const int MaxWebSocketTextMessageBytes = 16 * 1024;

        public static Uri BuildWebSocketEndpoint(string baseUrl)
        {
            Uri baseUri = new Uri(baseUrl, UriKind.Absolute);
            string scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";

            return new UriBuilder(baseUri)
            {
                Scheme = scheme,
                Path = "/ws/replication",
                Query = string.Empty
            }.Uri;
        }

        public static async Task<string?> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(8 * 1024);

            try
            {
                using MemoryStream stream = new MemoryStream();

                while (true)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(rented), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State == WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken).ConfigureAwait(false);
                        }

                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    stream.Write(rented, 0, result.Count);

                    if (stream.Length > MaxWebSocketTextMessageBytes)
                    {
                        throw new InvalidOperationException($"Replication websocket text message exceeded {MaxWebSocketTextMessageBytes} bytes.");
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static async Task TrySendAckAsync(WebSocket webSocket, ReplicationSignalAck ack, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(ack, jsonOptions);

            try
            {
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Intentionally ignored: peer may close immediately after sending signal.
            }
        }

        public static async Task TryCloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string statusDescription, CancellationToken cancellationToken)
        {
            if (webSocket.State != WebSocketState.Open && webSocket.State != WebSocketState.CloseReceived)
            {
                return;
            }

            try
            {
                await webSocket.CloseAsync(status, statusDescription, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort close; the peer may already have gone away.
            }
        }
    }
}
