/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing;

/// <summary>
/// Marker on a test class that needs a real Altruist server (HTTP + transports)
/// running for the duration of its <c>[Fact]</c>s. The framework boots a single
/// process-shared server lazily on the first <c>[AltruistIntegrationTest]</c>
/// class encountered, then keeps it alive until the test process exits.
///
/// <para><b>When to use this vs <see cref="AltruistTestAttribute"/></b>:
/// reach for <c>[AltruistIntegrationTest]</c> only when the test exercises the
/// network surface itself (TCP packet round-trip, HTTP signup/login, broadcast
/// routing, multi-client visibility). For service-level assertions
/// (vault writes, business logic), <c>[AltruistTest]</c> is faster and gives
/// per-test isolation.</para>
///
/// <para><b>Config.</b> Boot uses <c>config.yml</c> + an optional
/// <c>config-test.yml</c> overlay loaded from the test assembly's base
/// directory. Override host/port/database/etc. there.</para>
///
/// <para><b>Database.</b> The configured persistence provider is used as-is —
/// no per-class schema isolation in this mode (the live server's DI is bound to
/// one schema for its whole lifetime). Tests must use unique IDs (Guid) to
/// avoid colliding with parallel tests in the same assembly.</para>
///
/// <para><b>Injection.</b> Constructor parameters of type <see cref="TestHttpClient"/>,
/// <see cref="TestTcpClient"/>, <see cref="TestUdpClient"/>, <see cref="TestWebSocketClient"/>
/// and <see cref="TestPlayerSession"/> get a fresh instance each (per test case); any other
/// parameter resolves from the live server's container. Opt into
/// <c>Internal.AltruistTestCollectionFactory</c> to serialize integration classes against the shared server.</para>
/// <example>
/// <code>
/// [AltruistIntegrationTest]
/// public sealed class EchoTests
/// {
///     private readonly TestTcpClient _tcp;
///     public EchoTests(TestTcpClient tcp) =&gt; _tcp = tcp;
///
///     [Fact]
///     public async Task Echoes()
///     {
///         await _tcp.ConnectAsync();
///         await _tcp.SendAsync("echo", new { text = "hi" });
///         Assert.NotEmpty(await _tcp.DrainAsync(TimeSpan.FromSeconds(1)));
///     }
/// }
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AltruistIntegrationTestAttribute : Attribute
{
}
