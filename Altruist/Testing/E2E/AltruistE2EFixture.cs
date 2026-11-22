/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Configuration;

using Xunit;

namespace Altruist.Testing.E2E;

/// <summary>
/// Per-class fixture for <see cref="AltruistE2ETestAttribute"/> tests. Loads
/// <c>config.yml</c> + an optional <c>config.E2E.yml</c> overlay once per test
/// class, provides a single <see cref="ResetAsync"/> hook against the running
/// server's <c>/e2e/v1/reset</c> endpoint, and factory-builds per-test
/// <see cref="E2EClientStack"/> instances on demand.
///
/// <para>The fixture itself does <em>not</em> own a player session — each test
/// gets its own <see cref="E2EClientStack"/> via <see cref="NewClientStack"/>,
/// which owns one <see cref="TestPlayerSession"/>, one
/// <see cref="Altruist.Client.ClientPacketDispatcher"/>, and one
/// <see cref="Altruist.Client.Inventory.ClientInventoryService"/>. Disposing the
/// stack tears them all down.</para>
///
/// <para><b>Server requirement.</b> A real server must be running with
/// <c>altruist:e2e:enabled=true</c> (typically the
/// <c>docker-compose-e2e.yml</c> stack). Without the flag,
/// <see cref="ResetAsync"/> returns 404.</para>
///
/// <para><b>Test pattern</b>:</para>
/// <code>
/// [AltruistE2ETest]
/// public sealed class InventoryE2ESmokeTests : IClassFixture&lt;AltruistE2EFixture&gt;
/// {
///     readonly AltruistE2EFixture _e2e;
///     public InventoryE2ESmokeTests(AltruistE2EFixture e2e) => _e2e = e2e;
///
///     [Fact]
///     public async Task Login_populates_bag()
///     {
///         await _e2e.ResetAsync();
///         await using var stack = _e2e.NewClientStack();
///         stack.Inventory.RegisterContainer(new GridLayout(1, 5, 9));
///
///         await stack.Session.SetupPlayerAsync(race: 0);
///         await stack.PumpAsync(TimeSpan.FromSeconds(2));
///
///         Assert.NotNull(stack.Inventory.GetSnapshot(window: 1, cell: 0));
///     }
/// }
/// </code>
/// </summary>
public sealed class AltruistE2EFixture : IAsyncLifetime
{
    /// <summary>Resolved configuration: <c>config.yml</c> with
    /// <c>config.E2E.yml</c> overlaid on top (both optional, read from the test output directory).</summary>
    public IConfiguration Config { get; }

    /// <summary>Created by xUnit once per test class; loads the configuration.</summary>
    public AltruistE2EFixture()
    {
        Config = LoadConfigWithOverlay();
    }

    /// <summary>Does nothing (the fixture holds no connections).</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Does nothing (each <see cref="E2EClientStack"/> is disposed by its test).</summary>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Truncate every <c>[Vault]</c> table on the running server. Calls
    /// <c>POST /e2e/v1/reset</c> — only succeeds when the server is configured
    /// with <c>altruist:e2e:enabled=true</c>. Otherwise returns 404, which
    /// surfaces as <see cref="HttpRequestException"/>.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="HttpRequestException">The server refused the reset (E2E mode off) or is unreachable.</exception>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        using var http = new TestHttpClient(Config);
        var resp = await http.PostAsync("/e2e/v1/reset", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Build a fresh per-test client stack: a new
    /// <see cref="TestPlayerSession"/>, a fresh
    /// <see cref="Altruist.Client.ClientPacketDispatcher"/>, and a fresh
    /// <see cref="Altruist.Client.Inventory.ClientInventoryService"/> registered
    /// with the dispatcher. Caller owns the stack and disposes it via
    /// <c>await using</c>.</summary>
    public E2EClientStack NewClientStack() => new(Config);

    /// <summary>
    /// Loads <c>config.yml</c> (optional) and <c>config.E2E.yml</c> (optional)
    /// from the test assembly's output directory, with <c>config.E2E.yml</c>
    /// values taking precedence.
    ///
    /// <para>Naming matches the framework's <c>config.&lt;profile&gt;.yml</c>
    /// convention (see <see cref="AppConfigLoader.Load"/>) — <c>E2E</c> is the
    /// profile name. The fixture loads the two files directly rather than
    /// going through <see cref="AppConfigLoader"/> to (a) avoid mutating the
    /// global cached configuration that other tests in the same process may
    /// rely on, and (b) avoid coupling to <c>DOTNET_ENVIRONMENT</c> being set
    /// — the test framework decides which profile applies, not the shell.</para>
    /// </summary>
    private static IConfiguration LoadConfigWithOverlay()
    {
        var baseDir = AppContext.BaseDirectory;
        var basePath = Path.Combine(baseDir, "config.yml");
        var overlayPath = Path.Combine(baseDir, "config.E2E.yml");

        var builder = new ConfigurationBuilder();
        if (File.Exists(basePath))
            builder.AddYamlFile(basePath, optional: true, reloadOnChange: false);
        if (File.Exists(overlayPath))
            builder.AddYamlFile(overlayPath, optional: true, reloadOnChange: false);

        return builder.Build();
    }
}
