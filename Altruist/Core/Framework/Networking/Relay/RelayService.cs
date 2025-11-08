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


using Microsoft.Extensions.Logging;

namespace Altruist;


// [Service(typeof(IRelayService))]
// public class RelayPacketService : IRelayService
// {
//     private CancellationTokenSource _cts = new();
//     private readonly IConnectionManager _connectionManager;
//     private readonly ICodec _codec;

//     private ITransportClient _transportClient;

//     private readonly ILogger _logger;

//     public event Action? OnConnected;
//     public event Action<Exception> OnRetryExhausted = _ => { };
//     public event Action<Exception> OnFailed = _ => { };

//     public RelayPacketService(IConnectionManager connectionManager, ICodec codec, ILoggerFactory loggerFactory)
//     {
//         _connectionManager = connectionManager;
//         _codec = codec;
//         _logger = loggerFactory.CreateLogger<RelayPacketService>();
//     }

//     public async Task Relay(byte[] message)
//     {
//         AltruistPacket packet = _codec.Decoder.Decode<AltruistPacket>(message);
//         if (string.IsNullOrEmpty(packet.Event)) return;

//         await _connectionManager.ProcessPacket(packet, message, packet.Event, packet.Header.Receiver ?? "");
//     }

//     public async Task Relay(IPacket data)
//     {
//         if (_transportClient != null && _transportClient.IsConnected)
//         {
//             var encoded = _codec.Encoder.Encode(data);
//             await _transportClient.SendAsync(encoded);
//         }
//     }

//     public async Task ConnectAsync(
//          string protocol, string host, int port,
//         int maxRetries = 30, int delayMilliseconds = 2000)
//     {
//         if (_transportClient == null)
//             throw new InvalidOperationException("Transport client not set.");

//         string _gatewayUrl = $"{protocol}://{host}:{port}";
//         for (int attempt = 1; attempt <= maxRetries; attempt++)
//         {
//             try
//             {
//                 _logger.LogInformation($"🔌 Attempt {attempt}/{maxRetries}: Connecting to {_gatewayUrl}...");
//                 await _transportClient.ConnectAsync(_gatewayUrl);
//                 _logger.LogInformation($"✅ Connected to {_gatewayUrl}.");
//                 RaiseConnectedEvent();
//                 _ = ListenForMessages();
//                 return;
//             }
//             catch (Exception ex)
//             {
//                 _logger.LogError(ex, $"❌ Attempt {attempt} failed to connect to relay service: {ex.Message}");

//                 if (attempt < maxRetries)
//                 {
//                     _logger.LogInformation($"⏳ Retrying in {delayMilliseconds} ms...");
//                     await Task.Delay(delayMilliseconds);
//                 }
//                 else
//                 {
//                     _logger.LogCritical($"❗ Failed to connect after {maxRetries} attempts.");
//                     RaiseOnRetryExhaustedEvent(ex);
//                 }
//             }
//         }
//     }

//     private void RaiseConnectedEvent()
//     {
//         OnConnected?.Invoke();
//     }

//     private void RaiseFailedEvent(Exception ex)
//     {
//         OnFailed?.Invoke(ex);
//     }

//     private void RaiseOnRetryExhaustedEvent(Exception ex)
//     {
//         OnRetryExhausted?.Invoke(ex);
//     }

//     private async Task ListenForMessages()
//     {
//         while (_transportClient != null && _transportClient.IsConnected)
//         {
//             try
//             {
//                 var message = await _transportClient.ReceiveAsync(_cts.Token);
//                 await Relay(message);
//             }
//             catch (Exception ex)
//             {
//                 _logger.LogError($"Error receiving message: {ex.Message}");
//             }
//         }
//     }

// }