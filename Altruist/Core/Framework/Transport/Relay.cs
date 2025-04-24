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

namespace Altruist.Transport;

public abstract class RelayPortal : Portal
{
    private ICodec Codec { get; }

    public RelayPortal(IPortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        Codec = context.Codec;
    }

    public async Task Relay(byte[] message)
    {
        AltruistPacket packet = Codec.Decoder.Decode<AltruistPacket>(message);
        if (string.IsNullOrEmpty(packet.Event)) return;

        await ProcessPacket(packet, message, packet.Event, packet.Header.Receiver ?? "");
    }
}

public class AltruistRelayService : AbstractRelayService
{
    private readonly string _gatewayUrl;
    private readonly string _eventName;
    private ITransportClient _transportClient;
    private CancellationTokenSource _cts = new();

    private ICodec _codec { get; }

    private RelayPortal _socketPortal;

    private readonly ILogger _logger;

    public override string RelayEvent => _eventName;

    public override string ServiceName { get; } = "AltruistRelayService";

    public override bool IsConnected => throw new NotImplementedException();

    public AltruistRelayService(
        string protocol,
        string host, int port, string eventName, RelayPortal socketPortal, ICodec codec, ILoggerFactory loggerFactory, ITransportClient transportClient)
    {
        _gatewayUrl = $"{protocol}://{host}:{port}";
        _eventName = eventName;
        _socketPortal = socketPortal;
        _codec = codec;
        _logger = loggerFactory.CreateLogger<AltruistRelayService>();
        _transportClient = transportClient;
    }

    public override async Task Relay(IPacket data)
    {
        if (_transportClient != null && _transportClient.IsConnected)
        {
            var encoded = _codec.Encoder.Encode(data);
            await _transportClient.SendAsync(encoded);
        }
    }

    public override async Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000)
    {
        if (_transportClient == null)
            throw new InvalidOperationException("Transport client not set.");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation($"🔌 Attempt {attempt}/{maxRetries}: Connecting to {_gatewayUrl}...");
                await _transportClient.ConnectAsync(_gatewayUrl);
                _logger.LogInformation($"✅ Connected to {_gatewayUrl}.");
                RaiseConnectedEvent();
                _ = ListenForMessages();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Attempt {attempt} failed to connect to relay service: {ex.Message}");

                if (attempt < maxRetries)
                {
                    _logger.LogInformation($"⏳ Retrying in {delayMilliseconds} ms...");
                    await Task.Delay(delayMilliseconds);
                }
                else
                {
                    _logger.LogCritical($"❗ Failed to connect after {maxRetries} attempts.");
                    RaiseOnRetryExhaustedEvent(ex);
                }
            }
        }
    }


    private async Task ListenForMessages()
    {
        while (_transportClient != null && _transportClient.IsConnected)
        {
            try
            {
                var message = await _transportClient.ReceiveAsync(_cts.Token);
                await _socketPortal.Relay(message);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error receiving message: {ex.Message}");
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (_transportClient != null)
        {
            await _transportClient.DisconnectAsync();
        }
    }
}
