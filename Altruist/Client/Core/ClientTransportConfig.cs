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
    /// <c>"udp"</c>, <c>"ws"</c>.
    /// </summary>
    public string DefaultTransport { get; set; } = "tcp";

    public TransportEndpointOptions? Tcp { get; set; }
    public TransportEndpointOptions? Udp { get; set; }
    public TransportEndpointOptions? Ws { get; set; }
}

/// <summary>One transport's endpoint + codec configuration.</summary>
public sealed class TransportEndpointOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public CodecOptions Codec { get; set; } = new();
}

/// <summary>Codec selection for a single transport.</summary>
public sealed class CodecOptions
{
    public string Provider { get; set; } = "messagepack";
}
