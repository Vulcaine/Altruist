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
///
/// <para><b>Receiving:</b> inbound frames are not exposed here; they are dispatched to
/// <see cref="PacketAttribute"/> methods on <see cref="PacketHandlerAttribute"/> classes
/// through <see cref="ClientPacketDispatcher"/>.</para>
///
/// <para><b>Registration:</b> the default implementation <see cref="AltruistClientRouter"/>
/// is a DI singleton (<c>[Service(typeof(IAltruistClientRouter))]</c>) that only exists when
/// the <c>altruist:client:transport</c> section is present in <c>config.yml</c>
/// (bound to <see cref="ClientTransportConfig"/>).</para>
/// </summary>
/// <example>
/// <code>
/// // config.yml: altruist:client:transport:tcp: { host: 127.0.0.1, port: 5566 }
/// public sealed class LoginFlow
/// {
///     private readonly IAltruistClientRouter _router;
///     public LoginFlow(IAltruistClientRouter router) =&gt; _router = router;
///
///     public async Task RunAsync(CancellationToken ct)
///     {
///         _router.OnDisconnected += () =&gt; Console.WriteLine("lost connection");
///         await _router.ConnectAsync(ct: ct);          // background pumps dispatch inbound frames
///         await _router.SendAsync("login", new LoginPacket { User = "alice" }, ct);
///         await _router.Udp.SendAsync("move", new MovePacket { X = 1 }, ct); // explicit transport
///     }
/// }
///
/// // Unity-style main-thread dispatch instead of background pumps:
/// await router.ConnectAsync(autoPump: false);
/// void Update() =&gt; router.DrainInbound(64);
/// </code>
/// </example>
public interface IAltruistClientRouter
{
    /// <summary>
    /// Send via the configured default transport (<see cref="ClientTransportConfig.DefaultTransport"/>,
    /// <c>"tcp"</c> when unset). Use <see cref="Tcp"/> / <see cref="Udp"/> / <see cref="Ws"/>
    /// when a specific packet must go over a specific transport (e.g. lossy movement over UDP).
    /// </summary>
    /// <typeparam name="T">Concrete packet type; serialized with the transport's
    /// <see cref="IClientCodec"/> as <typeparamref name="T"/> (no runtime-type lookup).</typeparam>
    /// <param name="gate">Server gate name the packet is routed to (UTF-8, at most 255 bytes on TCP/UDP).</param>
    /// <param name="packet">Packet to send.</param>
    /// <param name="ct">Cancellation token for the send.</param>
    /// <exception cref="InvalidOperationException">The default transport is not configured, or the
    /// transport is not connected yet.</exception>
    Task SendAsync<T>(string gate, T packet, CancellationToken ct = default);

    /// <summary>TCP-specific sender (ordered, reliable). Throws <see cref="InvalidOperationException"/>
    /// on access if <c>altruist:client:transport:tcp</c> is not configured.</summary>
    ITransportSender Tcp { get; }

    /// <summary>UDP-specific sender (unordered, lossy; for high-frequency state). Throws
    /// <see cref="InvalidOperationException"/> on access if <c>altruist:client:transport:udp</c>
    /// is not configured.</summary>
    ITransportSender Udp { get; }

    /// <summary>WebSocket-specific sender. Throws <see cref="InvalidOperationException"/> on
    /// access if <c>altruist:client:transport:ws</c> is not configured.</summary>
    ITransportSender Ws { get; }

    /// <summary>
    /// Connect every transport declared in <see cref="ClientTransportConfig"/>.
    /// When <paramref name="autoPump"/> is <c>true</c> (default), the router spawns
    /// background tasks that drain inbound frames into the dispatcher. Set
    /// <c>false</c> in environments that must dispatch on a specific thread
    /// (Unity main-thread semantics) — call <see cref="DrainInbound"/> from the
    /// driver thread instead.
    /// </summary>
    /// <remarks>
    /// Connects TCP first (awaiting the server's client-id handshake), binds UDP to an
    /// OS-assigned local port, then connects WS, and raises <see cref="OnConnected"/>
    /// synchronously on the calling thread before returning. Not idempotent: calling it
    /// again on a connected TCP transport throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <param name="autoPump"><c>true</c> to dispatch inbound frames on background tasks;
    /// <c>false</c> to dispatch manually via <see cref="DrainInbound"/>.</param>
    /// <param name="ct">Cancels the connect and, linked, the receive pumps.</param>
    Task ConnectAsync(bool autoPump = true, CancellationToken ct = default);

    /// <summary>
    /// Pull up to <paramref name="maxFrames"/> inbound frames off each
    /// transport's queue and dispatch them synchronously on the calling thread.
    /// Returns total frames dispatched. Only meaningful when
    /// <see cref="ConnectAsync"/> was called with <c>autoPump: false</c>.
    /// </summary>
    /// <remarks>Only the TCP queue is drained; UDP and WebSocket frames are only
    /// delivered by the background pumps (<c>autoPump: true</c>).</remarks>
    /// <param name="maxFrames">Upper bound on frames dispatched in this call (per-frame budget).</param>
    /// <returns>Number of frames dispatched.</returns>
    int DrainInbound(int maxFrames);

    /// <summary>Disconnect every transport: cancels the receive pumps, closes sockets and
    /// awaits the pumps. Safe to call more than once; errors are swallowed.</summary>
    Task DisconnectAsync();

    /// <summary>Server-assigned client identity from the TCP handshake. Empty
    /// when TCP is not configured / not yet connected.</summary>
    string ClientId { get; }

    /// <summary>True when the TCP or WebSocket transport is connected (UDP is connectionless
    /// and does not count).</summary>
    bool IsConnected { get; }

    /// <summary>Fires at the end of <see cref="ConnectAsync"/>, on the calling thread.</summary>
    event Action? OnConnected;

    /// <summary>Fires when the TCP read loop ends or the WebSocket client is disposed
    /// (including during <see cref="DisconnectAsync"/>). May be raised on a background thread.</summary>
    event Action? OnDisconnected;

    /// <summary>Fires for errors caught in the TCP read loop or the UDP / WebSocket receive
    /// pumps (the failing pump stops afterwards). Raised on a background thread. Send errors
    /// are not routed here; they surface from the awaited send task.</summary>
    event Action<Exception>? OnError;
}
