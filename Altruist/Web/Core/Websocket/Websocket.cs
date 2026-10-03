/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Net.WebSockets;
using System.Text.Json.Serialization;

using Altruist.Security;

namespace Altruist.Web
{
    public sealed class WebSocketConnection : AltruistConnection
    {
        [JsonIgnore] private readonly WebSocket? _webSocket;

        /// <summary>Upper bound for one reassembled inbound message; larger messages close the connection.</summary>
        public const int MaxMessageBytes = 1024 * 1024;

        // WebSocket allows only one outstanding send per socket; sends come from several threads
        // (handlers, engine ticks, broadcasts), so they are serialized here.
        [JsonIgnore] private readonly SemaphoreSlim _sendLock = new(1, 1);

        [JsonIgnore] private byte[]? _receiveBuffer;

        [JsonPropertyName("IsConnected")]
        public override bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        public WebSocketConnection() { }

        public WebSocketConnection(WebSocket webSocket, string route, string remoteAddress, string connectionId, AuthDetails? authDetails)
        {
            _webSocket = webSocket;
            ConnectionId = connectionId;
            AuthDetails = authDetails;
            Route = route;
            RemoteAddress = remoteAddress ?? "";
            ConnectedAt = DateTime.UtcNow;
        }

        public override async Task SendAsync(byte[] data)
        {
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket is not open.");
            await _sendLock.WaitAsync();
            try
            {
                await _webSocket!.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public override async Task SendAsync(ReadOnlyMemory<byte> data)
        {
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket is not open.");
            await _sendLock.WaitAsync();
            try
            {
                await _webSocket!.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
                return Array.Empty<byte>();

            // One receive buffer per connection (only the read loop calls this). Allocating 4 KB
            // per message was ~80% of the server's allocations at 60 inputs/s per client.
            var buffer = _receiveBuffer ??= new byte[4096];
            var first = await _webSocket!.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (first.MessageType == WebSocketMessageType.Close)
            {
                await AcknowledgeCloseAsync();
                return Array.Empty<byte>();
            }
            if (first.EndOfMessage)
                return buffer.AsSpan(0, first.Count).ToArray();

            // A message may arrive in several frames; reassemble until EndOfMessage.
            using var message = new MemoryStream();
            message.Write(buffer, 0, first.Count);
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // The state is now CloseReceived (IsConnected is false, so CloseAsync would be a
                    // no-op): answer the peer's close frame, or browsers report an abnormal 1006 drop.
                    await AcknowledgeCloseAsync();
                    return Array.Empty<byte>();
                }
                if (message.Length + result.Count > MaxMessageBytes)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None);
                    return Array.Empty<byte>();
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return message.ToArray();
        }

        public override async Task CloseAsync()
        {
            if (IsConnected)
            {
                await _webSocket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
            }
        }

        private async Task AcknowledgeCloseAsync()
        {
            if (_webSocket is not { State: WebSocketState.CloseReceived })
                return;
            try
            {
                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // The peer went away mid-handshake; nothing left to acknowledge.
            }
        }

        public override void Abort() => _webSocket?.Abort();

        public override async Task CloseOutputAsync()
        {
            if (IsConnected)
            {
                await _webSocket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Output closed", CancellationToken.None);
            }
        }
    }

}
