namespace Altruist.Client;

/// <summary>
/// Bound from the <c>altruist:client:transport</c> section of <c>config.yml</c>.
/// Mirrors the server's <c>altruist:server:transport</c> shape — same vocabulary,
/// same binding pattern, so a developer who knows the server config doesn't need
/// to learn anything new.
///
/// <para><b>Default values</b> mean an unconfigured client targets
/// <c>127.0.0.1:0</c> with the messagepack codec — that's never useful in
/// production but allows the type to be DI-bound without forcing every client
/// app to enumerate every transport.</para>
///
/// <para>Each transport block that is present (non-null) brings up that transport:
/// <c>AltruistTcpClient</c>, <c>AltruistUdpClient</c> and <c>AltruistWebSocketClient</c> are
/// registered via <c>[ConditionalOnConfig("altruist:client:transport:tcp|udp|ws")]</c>, and
/// <see cref="AltruistClientRouter"/> itself only registers when the
/// <c>altruist:client:transport</c> section exists. The WebSocket URL is built as
/// <c>ws://{host}:{port}</c> (no TLS, no path).</para>
///
/// <example>
/// <code>
/// altruist:
///   client:
///     transport:
///       defaultTransport: tcp
///       tcp: { host: 127.0.0.1, port: 5566, codec: { provider: messagepack } }
///       udp: { host: 127.0.0.1, port: 5567, codec: { provider: messagepack } }
///       ws:  { host: 127.0.0.1, port: 5568, codec: { provider: json } }
/// </code>
/// </example>
/// </summary>
[ConfigurationProperties("altruist:client:transport")]
public sealed class ClientTransportConfig
{
    /// <summary>
    /// Which transport <see cref="IAltruistClientRouter.SendAsync"/> uses when
    /// the caller doesn't pick one explicitly. Valid values: <c>"tcp"</c>,
    /// <c>"udp"</c>, <c>"ws"</c> (also <c>"websocket"</c>); case-insensitive. Default <c>"tcp"</c>.
    /// Config key <c>altruist:client:transport:defaultTransport</c>.
    /// </summary>
    public string DefaultTransport { get; set; } = "tcp";

    /// <summary>TCP endpoint (<c>altruist:client:transport:tcp</c>); <c>null</c> disables TCP.
    /// TCP also supplies <see cref="IAltruistClientRouter.ClientId"/> via its handshake.</summary>
    public TransportEndpointOptions? Tcp { get; set; }
    /// <summary>UDP endpoint (<c>altruist:client:transport:udp</c>); <c>null</c> disables UDP.</summary>
    public TransportEndpointOptions? Udp { get; set; }
    /// <summary>WebSocket endpoint (<c>altruist:client:transport:ws</c>); <c>null</c> disables WebSocket.</summary>
    public TransportEndpointOptions? Ws { get; set; }
}

/// <summary>One transport's endpoint + codec configuration.</summary>
public sealed class TransportEndpointOptions
{
    /// <summary>Server host name or IP. Default <c>"127.0.0.1"</c>.</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>Server port. Default 0, which is never valid — always set it.</summary>
    public int Port { get; set; }
    /// <summary>Codec for this transport (<c>codec: { provider: ... }</c>); defaults to MessagePack.</summary>
    public CodecOptions Codec { get; set; } = new();
}

/// <summary>Codec selection for a single transport.</summary>
public sealed class CodecOptions
{
    /// <summary>
    /// <see cref="IClientCodec.Provider"/> name resolved through <see cref="ClientCodecResolver"/>:
    /// <c>"messagepack"</c> (default, alias <c>"msgpack"</c>), <c>"json"</c>, or a custom codec's name.
    /// </summary>
    public string Provider { get; set; } = "messagepack";
}
