/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// HTTP client pre-configured to talk to the Altruist test server. Inject one
/// (or more) as a ctor parameter on an <c>[AltruistIntegrationTest]</c> class:
///
/// <code>
/// public PartyTest(TestHttpClient http) { _http = http; }
///
/// [Fact]
/// public async Task Signup() =>
///     await _http.PostAsJsonAsync("/api/v1/auth/signup", new { username, email, password });
/// </code>
///
/// <para>The base address is resolved from <c>altruist:server:http:host</c> and
/// <c>altruist:server:http:port</c> (config.yml + config-test.yml overlay) at
/// boot time. Wildcard binds (<c>0.0.0.0</c>, <c>*</c>, <c>+</c>) are rewritten to
/// <c>localhost</c> for client-side use.</para>
///
/// <para>Subclasses <see cref="HttpClient"/>, so all standard methods —
/// <see cref="HttpClient.PostAsJsonAsync{TValue}(string?, TValue, System.Threading.CancellationToken)"/>,
/// <see cref="HttpClient.GetAsync(string?)"/>, etc. — are available with no
/// adapter layer.</para>
/// </summary>
public sealed class TestHttpClient : HttpClient
{
    public TestHttpClient(IConfiguration cfg)
    {
        var host = NormalizeHost(cfg["altruist:server:http:host"] ?? "localhost");
        var port = cfg["altruist:server:http:port"] ?? "8080";
        BaseAddress = new Uri($"http://{host}:{port}");
    }

    /// <summary>Wildcard server binds (e.g. <c>0.0.0.0</c>) aren't routable from a
    /// client; rewrite to <c>localhost</c>. Real hostnames pass through.</summary>
    internal static string NormalizeHost(string host) => host switch
    {
        "0.0.0.0" or "*" or "+" or "" => "localhost",
        _ => host,
    };
}
