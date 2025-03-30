using Microsoft.Extensions.Logging;

namespace Altruist.Transport;

public class RelayPortal : Portal
{
    private IMessageCodec Codec { get; }

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

public class AltruistRelayService : IRelayService
{
    private readonly string _gatewayUrl;
    private readonly string _eventName;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(30);
    private ITransportClient _transportClient;
    private CancellationTokenSource _cts = new();

    private IMessageCodec _codec { get; }

    private RelayPortal _socketPortal;

    private readonly ILogger _logger;

    public string RelayEvent => _eventName;

    public AltruistRelayService(
        string protocol,
        string host, int port, string eventName, RelayPortal socketPortal, IMessageCodec codec, ILoggerFactory loggerFactory, ITransportClient transportClient)
    {
        _gatewayUrl = $"{protocol}://{host}:{port}";
        _eventName = eventName;
        _socketPortal = socketPortal;
        _codec = codec;
        _logger = loggerFactory.CreateLogger<AltruistRelayService>();
        _transportClient = transportClient;
    }

    public async Task Relay(IPacket data)
    {
        if (_transportClient != null && _transportClient.IsConnected)
        {
            var encoded = _codec.Encoder.Encode(data);
            await _transportClient.SendAsync(encoded);
        }
    }

    public async Task ConnectAsync()
    {
        if (_transportClient == null)
            throw new InvalidOperationException("Transport client not set.");

        try
        {
            _logger.LogInformation($"Connecting to {_gatewayUrl}...");
            await _transportClient.ConnectAsync(_gatewayUrl);
            _logger.LogInformation($"Connected to {_gatewayUrl}.");

            _ = ListenForMessages();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to connect to the relay service {GetType().FullName}: {ex.Message}. Retrying in {_reconnectDelay.Seconds} seconds...");
            await Task.Delay(_reconnectDelay);
            await ConnectAsync();
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
