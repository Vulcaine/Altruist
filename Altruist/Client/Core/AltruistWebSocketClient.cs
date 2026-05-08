using System.Net.WebSockets;
using System.Text;

namespace Altruist.Client;

/// <summary>
/// WebSocket client for the Altruist wire protocol. Frames are messages —
/// no length prefix needed. Outbound shape is the same envelope the WS server
/// expects: <c>{ event, payload }</c>, codec-encoded.
///
/// <para>JSON codec → text frames; MessagePack codec → binary frames.</para>
/// </summary>
[Service]
[ConditionalOnConfig("altruist:client:transport:ws")]
internal sealed class AltruistWebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly Uri _url;
    private readonly IClientCodec _codec;
    private readonly ClientWebSocket _ws = new();
    private readonly WebSocketMessageType _messageType;

    public IClientCodec Codec => _codec;
    public bool IsConnected => _ws.State == WebSocketState.Open;

    public event Action? OnConnected;
    public event Action? OnDisconnected;

    /// <summary>Explicit ctor — used by tests.</summary>
    public AltruistWebSocketClient(Uri url, IClientCodec codec)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _messageType = codec.Provider == "json" ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
    }

    /// <summary>
    /// DI-friendly ctor. Reads the <c>altruist:client:transport:ws</c> block
    /// from <see cref="ClientTransportConfig"/> and resolves the codec via
    /// <see cref="ClientCodecResolver"/>.
    /// </summary>
    public AltruistWebSocketClient(ClientTransportConfig config, ClientCodecResolver codecResolver)
        : this(BuildUrl(config), ResolveCodec(config, codecResolver))
    { }

    private static Uri BuildUrl(ClientTransportConfig config)
    {
        var ws = config?.Ws ?? throw new InvalidOperationException(
            "altruist:client:transport:ws section missing — AltruistWebSocketClient cannot be constructed.");
        return new Uri($"ws://{ws.Host}:{ws.Port}");
    }

    private static IClientCodec ResolveCodec(ClientTransportConfig config, ClientCodecResolver codecResolver)
    {
        if (codecResolver is null) throw new ArgumentNullException(nameof(codecResolver));
        var ws = config?.Ws ?? throw new InvalidOperationException(
            "altruist:client:transport:ws section missing — AltruistWebSocketClient cannot be constructed.");
        return codecResolver.Resolve(ws.Codec.Provider);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_url, ct).ConfigureAwait(false);
        OnConnected?.Invoke();
    }

    /// <summary>Send <c>{ event, payload }</c> via the codec.</summary>
    public async Task SendAsync<T>(string gate, T payload, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException(
            "AltruistWebSocketClient not connected. Call ConnectAsync first.");

        var envelope = new Dictionary<string, object?>
        {
            ["event"] = gate,
            ["payload"] = payload,
        };
        var bytes = _codec.Serialize(envelope);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), _messageType, endOfMessage: true, ct)
                 .ConfigureAwait(false);
    }

    /// <summary>Receive one frame within <paramref name="timeout"/>. Returns
    /// <c>null</c> on timeout / close.</summary>
    public async Task<byte[]?> ReceiveAsync(TimeSpan timeout)
    {
        if (!IsConnected) return null;

        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[16384];
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            return ms.ToArray();
        }
        catch (OperationCanceledException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch { }
        OnDisconnected?.Invoke();
        Dispose();
    }

    public void Dispose() => _ws.Dispose();
}
