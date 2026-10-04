namespace Altruist.Client;

/// <summary>
/// Plain-data endpoint description. No DI, no IConfiguration coupling — consumers
/// build it however they want (config file, env var, hard-coded, etc.).
/// </summary>
public sealed record EndpointConfig(string Host, int Port, string CodecProvider = "messagepack");
