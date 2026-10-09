using System.Net.WebSockets;
using System.Text.Json;

namespace Altruist.Client;

/// <summary>
/// WebSocket client for the Altruist wire protocol. Frames are messages —
/// no length prefix needed. Outbound frames have the shape the server's read loop parses:
/// binary codecs send <c>[u8 gateLength][gate UTF-8][codec(payload)]</c> (like TCP and UDP), the JSON codec sends
/// the text frame <c>{"event": gate, "data": payload}</c>.
///
/// <para>JSON codec → text frames; any other codec → binary frames.</para>
///
/// <para>Internal: send through <see cref="IAltruistClientRouter.Ws"/>. Registered as a DI
/// singleton only when <c>altruist:client:transport:ws</c> is configured; the URL comes from
/// <see cref="TransportEndpointOptions.WebSocketUri"/> (<c>ws://</c>, or <c>wss://</c> with <c>secure: true</c>).</para>
/// </summary>
[Service]
[ConditionalOnConfig("altruist:client:transport:ws")]
internal sealed class AltruistWebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly Uri _url;
    private readonly IClientCodec _codec;
    private readonly ClientWebSocket _ws = new();
    private readonly bool _json;
    private Task<byte[]?>? _pendingReceive;
    private int _disconnectRaised;

    /// <summary>Codec used to encode outbound messages (and by the router to decode inbound ones).</summary>
    public IClientCodec Codec => _codec;
    /// <summary>True while the socket state is <see cref="WebSocketState.Open"/>.</summary>
    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>Fires after <see cref="ConnectAsync"/> completes.</summary>
    public event Action? OnConnected;
    /// <summary>Fires once when the connection ends: the server closed it, a receive failed, or <see cref="DisposeAsync"/> ran.</summary>
    public event Action? OnDisconnected;

    /// <summary>Explicit ctor — used by tests.</summary>
    /// <param name="url">Server WebSocket URL (<c>ws://</c> or <c>wss://</c>).</param>
    /// <param name="codec">Codec; provider <c>"json"</c> (any case) selects text frames, anything else binary.</param>
    public AltruistWebSocketClient(Uri url, IClientCodec codec)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _json = string.Equals(codec.Provider, "json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DI-friendly ctor. Reads the <c>altruist:client:transport:ws</c> block
    /// from <see cref="ClientTransportConfig"/> and resolves the codec via
    /// <see cref="ClientCodecResolver"/>. See <see cref="AltruistTcpClient"/>
    /// for why <see cref="Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"/>
    /// is required (same-arity ctor tie-break).
    /// </summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AltruistWebSocketClient(ClientTransportConfig config, ClientCodecResolver codecResolver)
        : this(BuildUrl(config), ResolveCodec(config, codecResolver))
    { }

    private static Uri BuildUrl(ClientTransportConfig config)
    {
        var ws = config?.Ws ?? throw new InvalidOperationException(
            "altruist:client:transport:ws section missing — AltruistWebSocketClient cannot be constructed.");
        return ws.WebSocketUri();
    }

    private static IClientCodec ResolveCodec(ClientTransportConfig config, ClientCodecResolver codecResolver)
    {
        if (codecResolver is null) throw new ArgumentNullException(nameof(codecResolver));
        var ws = config?.Ws ?? throw new InvalidOperationException(
            "altruist:client:transport:ws section missing — AltruistWebSocketClient cannot be constructed.");
        return codecResolver.Resolve(ws.Codec.Provider);
    }

    /// <summary>Opens the WebSocket and raises <see cref="OnConnected"/>.</summary>
    /// <param name="ct">Cancels the connect.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_url, ct).ConfigureAwait(false);
        OnConnected?.Invoke();
    }

    /// <summary>Send one packet to <paramref name="gate"/> (see the type summary for the frame shapes).</summary>
    /// <exception cref="ArgumentException"><paramref name="gate"/> is empty or longer than 127 UTF-8 bytes (the server's limit).</exception>
    public async Task SendAsync<T>(string gate, T payload, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException(
            "AltruistWebSocketClient not connected. Call ConnectAsync first.");

        var gateBytes = ClientFrame.GateBytes(gate);
        var body = _codec.Serialize(payload);
        var frame = _json ? JsonFrame(gate, body) : ClientFrame.Build(gateBytes, body);
        await _ws.SendAsync(new ArraySegment<byte>(frame), _json ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            endOfMessage: true, ct).ConfigureAwait(false);
    }

    // {"event": gate, "data": <payload JSON as the codec wrote it>}
    private static byte[] JsonFrame(string gate, byte[] payloadJson)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("event", gate);
            writer.WritePropertyName("data");
            if (payloadJson.Length == 0)
            {
                writer.WriteNullValue();
            }
            else
            {
                using var doc = JsonDocument.Parse(payloadJson);
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Receive one frame within <paramref name="timeout"/>. Returns <c>null</c> on timeout or close; a close by the
    /// server or a failed receive raises <see cref="OnDisconnected"/>.
    /// </summary>
    /// <remarks>
    /// A timeout leaves the socket open and the receive pending (cancelling a <see cref="ClientWebSocket"/> receive
    /// would abort the socket); the next call picks it up. Not safe for concurrent callers (one receive pump per client).
    /// </remarks>
    public async Task<byte[]?> ReceiveAsync(TimeSpan timeout)
    {
        if (_pendingReceive is null && !IsConnected) return null;

        var receive = _pendingReceive ??= ReceiveMessageAsync();
        if (receive != await Task.WhenAny(receive, Task.Delay(timeout)).ConfigureAwait(false))
            return null;
        _pendingReceive = null;
        return await receive.ConfigureAwait(false);
    }

    private async Task<byte[]?> ReceiveMessageAsync()
    {
        var buf = new byte[16384];
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RaiseDisconnected();
                    return null;
                }
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            RaiseDisconnected();
            return null;
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
            OnDisconnected?.Invoke();
    }

    /// <summary>Closes the socket gracefully if open, raises <see cref="OnDisconnected"/> (once), then disposes.</summary>
    /// <returns>A task that completes when the socket is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client done", CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        RaiseDisconnected();
        Dispose();
    }

    /// <summary>Disposes the socket without a close handshake and without raising <see cref="OnDisconnected"/>.</summary>
    public void Dispose() => _ws.Dispose();
}
