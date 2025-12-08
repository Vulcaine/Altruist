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

        [JsonPropertyName("IsConnected")]
        public override bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        public WebSocketConnection() { } // for json

        public WebSocketConnection(WebSocket webSocket, string remoteAddress, string connectionId, AuthDetails? authDetails)
        {
            _webSocket = webSocket;
            ConnectionId = connectionId;
            AuthDetails = authDetails;
            RemoteAddress = remoteAddress ?? "";
            ConnectedAt = DateTime.UtcNow;
        }

        public override async Task SendAsync(byte[] data)
        {
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket is not open.");
            await _webSocket!.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
                return Array.Empty<byte>();

            var buffer = new byte[4096];
            var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync();
                return Array.Empty<byte>();
            }

            return buffer.Take(result.Count).ToArray();
        }

        public override async Task CloseAsync()
        {
            if (IsConnected)
            {
                await _webSocket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
            }
        }

        public override async Task CloseOutputAsync()
        {
            if (IsConnected)
            {
                await _webSocket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Output closed", CancellationToken.None);
            }
        }
    }

}
