using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Altruist.Client;

/// <summary>
/// UDP client speaking the Altruist wire protocol.
///
/// <para>UDP datagrams are inherently framed, so there's no length prefix.
/// Outbound: <c>[1 byte gateLen][gate UTF-8][codec(packet)]</c>. Inbound: server
/// sends a codec-encoded <see cref="MessageEnvelope"/> per datagram.</para>
///
/// <para>Use for high-frequency lossy traffic — movement deltas, voice, etc.
/// Server identifies the client by source endpoint; no handshake.</para>
///
/// <para>Internal: send through <see cref="IAltruistClientRouter.Udp"/>. Registered as a DI
/// singleton only when <c>altruist:client:transport:udp</c> is configured.</para>
/// </summary>
[Service]
[ConditionalOnConfig("altruist:client:transport:udp")]
internal sealed class AltruistUdpClient : IAsyncDisposable, IDisposable
{
    private readonly EndpointConfig _endpoint;
    private readonly IClientCodec _codec;
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _serverEndpoint;
    private Task<UdpReceiveResult>? _pendingReceive;

    /// <summary>Codec used to encode outbound packets (and by the router to decode inbound ones).</summary>
    public IClientCodec Codec => _codec;

    /// <summary>
    /// Explicit ctor for manual wiring. Resolves <see cref="EndpointConfig.Host"/> synchronously
    /// via DNS to its first IPv4 address (<c>"localhost"</c> maps to loopback); falls back to
    /// loopback when no IPv4 address is found.
    /// </summary>
    /// <param name="endpoint">Server host and port.</param>
    /// <param name="codec">Codec used to encode outbound packets.</param>
    public AltruistUdpClient(EndpointConfig endpoint, IClientCodec codec)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));

        var hostIp = endpoint.Host == "localhost"
            ? IPAddress.Loopback
            : Dns.GetHostAddresses(endpoint.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? IPAddress.Loopback;
        _serverEndpoint = new IPEndPoint(hostIp, endpoint.Port);
    }

    /// <summary>
    /// DI-friendly ctor. Reads the <c>altruist:client:transport:udp</c> block
    /// from <see cref="ClientTransportConfig"/> and resolves the codec via
    /// <see cref="ClientCodecResolver"/>. See <see cref="AltruistTcpClient"/>
    /// for why <see cref="Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"/>
    /// is required (same-arity ctor tie-break).
    /// </summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AltruistUdpClient(ClientTransportConfig config, ClientCodecResolver codecResolver)
        : this(BuildEndpoint(config), ResolveCodec(config, codecResolver))
    { }

    private static EndpointConfig BuildEndpoint(ClientTransportConfig config)
    {
        var udp = config?.Udp ?? throw new InvalidOperationException(
            "altruist:client:transport:udp section missing — AltruistUdpClient cannot be constructed.");
        return new EndpointConfig(udp.Host, udp.Port, udp.Codec.Provider);
    }

    private static IClientCodec ResolveCodec(ClientTransportConfig config, ClientCodecResolver codecResolver)
    {
        if (codecResolver is null) throw new ArgumentNullException(nameof(codecResolver));
        var udp = config?.Udp ?? throw new InvalidOperationException(
            "altruist:client:transport:udp section missing — AltruistUdpClient cannot be constructed.");
        return codecResolver.Resolve(udp.Codec.Provider);
    }

    /// <summary>Bind a local port so the client can receive datagrams. Optional —
    /// fire-and-forget tests don't need it. Pass <c>0</c> to let the OS assign.</summary>
    public void Bind(int localPort = 0)
    {
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
    }

    /// <summary>Send one datagram with a gate name + codec-encoded payload.</summary>
    /// <remarks>A datagram send does not wait for the peer, so <paramref name="ct"/> is only checked before sending.</remarks>
    /// <typeparam name="T">Static packet type used for serialization.</typeparam>
    /// <param name="gate">Server gate name (1..127 UTF-8 bytes).</param>
    /// <param name="payload">Packet to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="gate"/> is empty or longer than 127 UTF-8 bytes (the server's limit).</exception>
    public async Task SendAsync<T>(string gate, T payload, CancellationToken ct = default)
    {
        var datagram = ClientFrame.Build(ClientFrame.GateBytes(gate), _codec.Serialize(payload));
        ct.ThrowIfCancellationRequested();
        await _udp.SendAsync(datagram, datagram.Length, _serverEndpoint).ConfigureAwait(false);
    }

    /// <summary>Receive one datagram or throw <see cref="OperationCanceledException"/>
    /// on timeout.</summary>
    /// <remarks>A timeout leaves the socket open and the receive pending: the next call picks it up, so a datagram
    /// arriving after a timeout is not lost. Not safe for concurrent callers (one receive pump per client).</remarks>
    /// <param name="timeout">Maximum wait.</param>
    /// <returns>The datagram bytes (one <see cref="MessageEnvelope"/>).</returns>
    public async Task<byte[]> ReceiveAsync(TimeSpan timeout)
    {
        // Neither target cancels a UDP receive without closing the socket (netstandard2.1 has no
        // token overload), so a timed-out receive stays pending for the next call instead.
        var receive = _pendingReceive ??= _udp.ReceiveAsync();
        if (receive != await Task.WhenAny(receive, Task.Delay(timeout)).ConfigureAwait(false))
            throw new OperationCanceledException($"No datagram within {timeout}.");
        _pendingReceive = null;
        return (await receive.ConfigureAwait(false)).Buffer;
    }

    /// <summary>Synchronous <see cref="Dispose"/> wrapped in a completed task.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    /// <summary>Closes and disposes the socket. Errors are swallowed.</summary>
    public void Dispose()
    {
        try { _udp.Close(); } catch { }
        try { _udp.Dispose(); } catch { }
    }
}
