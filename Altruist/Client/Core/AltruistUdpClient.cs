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
/// </summary>
[Service]
[ConditionalOnConfig("altruist:client:transport:udp")]
internal sealed class AltruistUdpClient : IAsyncDisposable, IDisposable
{
    private readonly EndpointConfig _endpoint;
    private readonly IClientCodec _codec;
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _serverEndpoint;

    public IClientCodec Codec => _codec;

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
    /// <see cref="ClientCodecResolver"/>.
    /// </summary>
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
    public async Task SendAsync<T>(string gate, T payload, CancellationToken ct = default)
    {
        var gateBytes = Encoding.UTF8.GetBytes(gate);
        if (gateBytes.Length > byte.MaxValue) throw new InvalidOperationException(
            $"Gate name too long ({gateBytes.Length} bytes); max is 255.");

        var payloadBytes = _codec.Serialize(payload);
        var datagram = new byte[1 + gateBytes.Length + payloadBytes.Length];
        datagram[0] = (byte)gateBytes.Length;
        Buffer.BlockCopy(gateBytes, 0, datagram, 1, gateBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, datagram, 1 + gateBytes.Length, payloadBytes.Length);

#if NETSTANDARD2_1
        // ns2.1 SendAsync takes (datagram, len, endpoint) — no token overload.
        using var reg = ct.Register(() => _udp.Close());
        await _udp.SendAsync(datagram, datagram.Length, _serverEndpoint).ConfigureAwait(false);
#else
        await _udp.SendAsync(datagram, _serverEndpoint, ct).ConfigureAwait(false);
#endif
    }

    /// <summary>Receive one datagram or throw <see cref="OperationCanceledException"/>
    /// on timeout.</summary>
    public async Task<byte[]> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
#if NETSTANDARD2_1
        using var reg = cts.Token.Register(() => _udp.Close());
        var result = await _udp.ReceiveAsync().ConfigureAwait(false);
#else
        var result = await _udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
#endif
        return result.Buffer;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        try { _udp.Close(); } catch { }
        try { _udp.Dispose(); } catch { }
    }
}
