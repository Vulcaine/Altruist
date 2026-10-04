namespace Altruist.Client;

/// <summary>
/// Client-side counterpart of the server's <c>IAltruistRouter</c>. Single DI
/// service that user code injects to send packets and observe connection
/// lifecycle. The transport implementations themselves
/// (<c>AltruistTcpClient</c>, <c>AltruistUdpClient</c>,
/// <c>AltruistWebSocketClient</c>) are <c>internal</c> and never directly
/// injected — the router owns them.
///
/// <para><b>Send patterns:</b></para>
/// <list type="bullet">
///   <item><c>router.SendAsync(gate, packet)</c> — uses
///   <c>ClientTransportConfig.DefaultTransport</c>.</item>
///   <item><c>router.Tcp.SendAsync(gate, packet)</c> /
///   <c>router.Udp.SendAsync(...)</c> / <c>router.Ws.SendAsync(...)</c> — picks
///   the transport explicitly.</item>
/// </list>
///
/// <para><b>Lifecycle:</b> <see cref="ConnectAsync"/> brings up every transport
/// declared in <see cref="ClientTransportConfig"/>; <see cref="OnConnected"/>
/// fires once after all configured transports finish their handshakes.
/// <see cref="OnDisconnected"/> fires when any configured transport drops; a
/// re-connect needs an explicit <see cref="ConnectAsync"/> from the consumer.</para>
/// </summary>
public interface IAltruistClientRouter
{
    /// <summary>Send via the configured default transport.</summary>
    Task SendAsync<T>(string gate, T packet, CancellationToken ct = default);

    /// <summary>TCP-specific sender. Throws if TCP is not configured.</summary>
    ITransportSender Tcp { get; }

    /// <summary>UDP-specific sender. Throws if UDP is not configured.</summary>
    ITransportSender Udp { get; }

    /// <summary>WebSocket-specific sender. Throws if WS is not configured.</summary>
    ITransportSender Ws { get; }

    /// <summary>
    /// Connect every transport declared in <see cref="ClientTransportConfig"/>.
    /// When <paramref name="autoPump"/> is <c>true</c> (default), the router spawns
    /// background tasks that drain inbound frames into the dispatcher. Set
    /// <c>false</c> in environments that must dispatch on a specific thread
    /// (Unity main-thread semantics) — call <see cref="DrainInbound"/> from the
    /// driver thread instead.
    /// </summary>
    Task ConnectAsync(bool autoPump = true, CancellationToken ct = default);

    /// <summary>
    /// Pull up to <paramref name="maxFrames"/> inbound frames off each
    /// transport's queue and dispatch them synchronously on the calling thread.
    /// Returns total frames dispatched. Only meaningful when
    /// <see cref="ConnectAsync"/> was called with <c>autoPump: false</c>.
    /// </summary>
    int DrainInbound(int maxFrames);

    /// <summary>Disconnect every transport.</summary>
    Task DisconnectAsync();

    /// <summary>Server-assigned client identity from the TCP handshake. Empty
    /// when TCP is not configured / not yet connected.</summary>
    string ClientId { get; }

    /// <summary>True when at least one transport is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Fires after <see cref="ConnectAsync"/> completes.</summary>
    event Action? OnConnected;

    /// <summary>Fires when any configured transport drops.</summary>
    event Action? OnDisconnected;

    /// <summary>Fires for caught read-loop / send errors.</summary>
    event Action<Exception>? OnError;
}
