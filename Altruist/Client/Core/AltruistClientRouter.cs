namespace Altruist.Client;

/// <summary>
/// Default <see cref="IAltruistClientRouter"/>. Owns the per-transport client
/// instances and the receive pumps that feed
/// <see cref="ClientPacketDispatcher.Dispatch"/>.
///
/// <para>Registered conditionally — the service only materialises when
/// <c>altruist:client:transport</c> is present in <c>config.yml</c>. Tests that
/// don't use DI construct it directly.</para>
///
/// <para>Disposes everything: cancels read pumps, closes sockets, drops the
/// dispatcher reference (the dispatcher itself is a separate singleton that
/// outlives the router).</para>
///
/// <para>Inject <see cref="IAltruistClientRouter"/> rather than this class. Construct it
/// directly only outside DI (tests, programmatic setup).</para>
/// </summary>
[Service(typeof(IAltruistClientRouter))]
[ConditionalOnConfig("altruist:client:transport")]
public sealed class AltruistClientRouter : IAltruistClientRouter, IAsyncDisposable
{
    private readonly ClientTransportConfig _config;
    private readonly ClientPacketDispatcher _dispatcher;

    private readonly AltruistTcpClient? _tcp;
    private readonly AltruistUdpClient? _udp;
    private readonly AltruistWebSocketClient? _ws;

    private readonly IClientCodec? _tcpCodec;
    private readonly IClientCodec? _udpCodec;
    private readonly IClientCodec? _wsCodec;

    private readonly ITransportSender? _tcpSender;
    private readonly ITransportSender? _udpSender;
    private readonly ITransportSender? _wsSender;

    private CancellationTokenSource? _readCts;
    private Task? _tcpPump;
    private Task? _udpPump;
    private Task? _wsPump;

    /// <inheritdoc/>
    public string ClientId => _tcp?.ClientId ?? string.Empty;
    /// <inheritdoc/>
    public bool IsConnected => (_tcp?.IsConnected ?? false) || (_ws?.IsConnected ?? false);

    /// <inheritdoc/>
    public ITransportSender Tcp => _tcpSender ?? throw new InvalidOperationException(
        "TCP transport is not configured (set 'altruist:client:transport:tcp' in config.yml).");
    /// <inheritdoc/>
    public ITransportSender Udp => _udpSender ?? throw new InvalidOperationException(
        "UDP transport is not configured (set 'altruist:client:transport:udp' in config.yml).");
    /// <inheritdoc/>
    public ITransportSender Ws => _wsSender ?? throw new InvalidOperationException(
        "WebSocket transport is not configured (set 'altruist:client:transport:ws' in config.yml).");

    /// <inheritdoc/>
    public event Action? OnConnected;
    /// <inheritdoc/>
    public event Action? OnDisconnected;
    /// <inheritdoc/>
    public event Action<Exception>? OnError;

    /// <summary>
    /// Creates the router. Transport clients are taken from <paramref name="services"/> when
    /// it can resolve them (each transport is its own conditional DI service); any transport
    /// that has a config block but was not resolved is built manually from
    /// <paramref name="config"/> with a codec from <paramref name="codecResolver"/>.
    /// </summary>
    /// <param name="config">Bound <c>altruist:client:transport</c> section.</param>
    /// <param name="codecResolver">Resolves each transport's <see cref="CodecOptions.Provider"/>.</param>
    /// <param name="dispatcher">Dispatcher that receives every inbound frame.</param>
    /// <param name="services">Optional container used to resolve the transport clients; <c>null</c> outside DI.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/>, <paramref name="codecResolver"/> or <paramref name="dispatcher"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var config = new ClientTransportConfig
    /// {
    ///     Tcp = new TransportEndpointOptions { Host = "127.0.0.1", Port = 5566 },
    /// };
    /// var dispatcher = new ClientPacketDispatcher();
    /// dispatcher.Register(new MyHandlers());
    /// var router = new AltruistClientRouter(
    ///     config, new ClientCodecResolver(new IClientCodec[] { new MessagePackClientCodec() }), dispatcher);
    /// await router.ConnectAsync();
    /// </code>
    /// </example>
    public AltruistClientRouter(
        ClientTransportConfig config,
        ClientCodecResolver codecResolver,
        ClientPacketDispatcher dispatcher,
        IServiceProvider? services = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (codecResolver is null) throw new ArgumentNullException(nameof(codecResolver));

        // Prefer DI-resolved transports when a container is available — each
        // transport class is its own [Service] with [ConditionalOnConfig], so
        // the container only constructs the transports that have a config
        // block. Fall back to manual construction when called outside DI
        // (tests, ad-hoc programmatic setup).

        _tcp = services?.GetService(typeof(AltruistTcpClient)) as AltruistTcpClient;
        _udp = services?.GetService(typeof(AltruistUdpClient)) as AltruistUdpClient;
        _ws = services?.GetService(typeof(AltruistWebSocketClient)) as AltruistWebSocketClient;

        if (_tcp is null && _config.Tcp is { } tcpConf)
        {
            var codec = codecResolver.Resolve(tcpConf.Codec.Provider);
            _tcp = new AltruistTcpClient(
                new EndpointConfig(tcpConf.Host, tcpConf.Port, tcpConf.Codec.Provider),
                codec);
        }

        if (_udp is null && _config.Udp is { } udpConf)
        {
            var codec = codecResolver.Resolve(udpConf.Codec.Provider);
            _udp = new AltruistUdpClient(
                new EndpointConfig(udpConf.Host, udpConf.Port, udpConf.Codec.Provider),
                codec);
        }

        if (_ws is null && _config.Ws is { } wsConf)
        {
            var codec = codecResolver.Resolve(wsConf.Codec.Provider);
            _ws = new AltruistWebSocketClient(
                new Uri($"ws://{wsConf.Host}:{wsConf.Port}"),
                codec);
        }

        if (_tcp is not null)
        {
            _tcpCodec = _tcp.Codec;
            _tcp.OnError += ex => OnError?.Invoke(ex);
            _tcp.OnDisconnected += () => OnDisconnected?.Invoke();
            _tcpSender = new TcpSender(_tcp);
        }
        if (_udp is not null)
        {
            _udpCodec = _udp.Codec;
            _udpSender = new UdpSender(_udp);
        }
        if (_ws is not null)
        {
            _wsCodec = _ws.Codec;
            _ws.OnDisconnected += () => OnDisconnected?.Invoke();
            _wsSender = new WsSender(_ws);
        }
    }

    /// <inheritdoc/>
    public Task SendAsync<T>(string gate, T packet, CancellationToken ct = default)
    {
        var sender = ResolveDefaultSender();
        return sender.SendAsync(gate, packet, ct);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(bool autoPump = true, CancellationToken ct = default)
    {
        if (_tcp is not null) await _tcp.ConnectAsync(ct).ConfigureAwait(false);
        _udp?.Bind(0);
        if (_ws is not null) await _ws.ConnectAsync(ct).ConfigureAwait(false);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _readCts.Token;

        // TCP read loop always runs on its own thread (the network buffer must be
        // drained or kernel buffers fill up). What's optional is the background
        // pump that hands frames to the dispatcher — Unity-shape consumers want
        // to do that on the main thread via DrainInbound.
        if (_tcp is not null) _tcp.StartReadLoop();

        if (autoPump)
        {
            if (_tcp is not null) _tcpPump = Task.Run(() => TcpDrainPump(token), token);
            if (_udp is not null) _udpPump = Task.Run(() => UdpReceivePump(token), token);
            if (_ws is not null) _wsPump = Task.Run(() => WsReceivePump(token), token);
        }

        OnConnected?.Invoke();
    }

    /// <inheritdoc/>
    public int DrainInbound(int maxFrames)
    {
        int dispatched = 0;
        if (_tcp is not null && _tcpCodec is not null)
        {
            while (dispatched < maxFrames && _tcp.IncomingQueue.TryDequeue(out var bytes))
            {
                _dispatcher.Dispatch(bytes, _tcpCodec);
                dispatched++;
            }
        }
        // UDP and WS are receive-loop based (no queue); they can only be drained
        // via autoPump. If a Unity consumer ever needs them, a parallel queue
        // would land here.
        return dispatched;
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        try { _readCts?.Cancel(); } catch { }

        try { _tcp?.Disconnect(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { if (_ws is not null) await _ws.DisposeAsync().ConfigureAwait(false); } catch { }

        // Drain pump tasks; ignore errors so DisconnectAsync is safe to call multiple times.
        await SafeAwait(_tcpPump).ConfigureAwait(false);
        await SafeAwait(_udpPump).ConfigureAwait(false);
        await SafeAwait(_wsPump).ConfigureAwait(false);

        _tcpPump = _udpPump = _wsPump = null;
        try { _readCts?.Dispose(); } catch { }
        _readCts = null;
    }

    /// <summary>Same as <see cref="DisconnectAsync"/>.</summary>
    /// <returns>A task that completes when every transport is closed.</returns>
    public ValueTask DisposeAsync() => new(DisconnectAsync());

    private ITransportSender ResolveDefaultSender()
    {
        var pick = (_config.DefaultTransport ?? "tcp").Trim().ToLowerInvariant();
        var sender = pick switch
        {
            "tcp" => _tcpSender,
            "udp" => _udpSender,
            "ws" or "websocket" => _wsSender,
            _ => null,
        };
        return sender ?? throw new InvalidOperationException(
            $"DefaultTransport '{pick}' is not configured.");
    }

    private async Task TcpDrainPump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_tcp!.IncomingQueue.TryDequeue(out var bytes))
                _dispatcher.Dispatch(bytes, _tcpCodec!);
            try { await Task.Delay(5, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task UdpReceivePump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytes = await _udp!.ReceiveAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                if (bytes is { Length: > 0 }) _dispatcher.Dispatch(bytes, _udpCodec!);
            }
            catch (OperationCanceledException) { /* timeout — keep polling */ }
            catch (Exception ex) { OnError?.Invoke(ex); return; }
        }
    }

    private async Task WsReceivePump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytes = await _ws!.ReceiveAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                if (bytes is { Length: > 0 }) _dispatcher.Dispatch(bytes, _wsCodec!);
            }
            catch (Exception ex) { OnError?.Invoke(ex); return; }
        }
    }

    private static async Task SafeAwait(Task? t)
    {
        if (t is null) return;
        try { await t.ConfigureAwait(false); } catch { }
    }

    // Per-transport ITransportSender implementations. Each holds a strongly-typed
    // reference to the underlying transport client so the generic <c>SendAsync&lt;T&gt;</c>
    // call preserves <typeparamref name="T"/> end-to-end (a <see cref="Func{T1,T2,T3,TResult}"/>
    // wrapper would erase T to <see cref="IPacketBase"/> and serialize through the
    // interface — which we definitely don't want on the wire).

    private sealed class TcpSender : ITransportSender
    {
        private readonly AltruistTcpClient _tcp;
        public TcpSender(AltruistTcpClient tcp) => _tcp = tcp;
        public Task SendAsync<T>(string gate, T packet, CancellationToken ct = default)
            => _tcp.SendAsync(gate, packet, ct);
    }

    private sealed class UdpSender : ITransportSender
    {
        private readonly AltruistUdpClient _udp;
        public UdpSender(AltruistUdpClient udp) => _udp = udp;
        public Task SendAsync<T>(string gate, T packet, CancellationToken ct = default)
            => _udp.SendAsync(gate, packet, ct);
    }

    private sealed class WsSender : ITransportSender
    {
        private readonly AltruistWebSocketClient _ws;
        public WsSender(AltruistWebSocketClient ws) => _ws = ws;
        public Task SendAsync<T>(string gate, T packet, CancellationToken ct = default)
            => _ws.SendAsync(gate, packet, ct);
    }
}
