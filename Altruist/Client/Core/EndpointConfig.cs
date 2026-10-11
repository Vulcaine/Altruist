namespace Altruist.Client;

/// <summary>
/// Plain-data endpoint description. No DI, no IConfiguration coupling — consumers
/// build it however they want (config file, env var, hard-coded, etc.).
/// </summary>
/// <remarks>
/// Used when constructing the transport clients by hand (tests, programmatic setup). In a
/// DI app configure <see cref="ClientTransportConfig"/> via <c>config.yml</c> instead.
/// </remarks>
/// <param name="Host">Server host name or IP.</param>
/// <param name="Port">Server port.</param>
/// <param name="CodecProvider">Codec provider name (informational; the transport uses the codec instance it is given).</param>
public sealed record EndpointConfig(string Host, int Port, string CodecProvider = "messagepack");
