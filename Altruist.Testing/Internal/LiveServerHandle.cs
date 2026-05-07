/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Testing.Internal;

/// <summary>
/// Process-singleton orchestrator for the live <c>[AltruistIntegrationTest]</c>
/// server. Boots once on first call to <see cref="EnsureStartedAsync"/>, holds the
/// running <see cref="WebApplication"/> for the rest of the test process, shuts
/// down on AppDomain exit.
///
/// <para><b>Why singleton.</b> Altruist's listener machinery binds to fixed ports
/// from <c>config.yml</c>; running two servers in one process collides. xUnit can
/// run test classes in parallel, so we must coordinate. The cheap answer is one
/// shared server per assembly run — pre-Phase-C tests already worked this way
/// (via <c>[Collection(DbCollection.Name)]</c>) and Guid-based test IDs prevent
/// state collisions across parallel <c>[Fact]</c>s.</para>
///
/// <para><b>Boot path.</b> Mirrors <c>AltruistBootstrap.Bootstrap()</c> against a
/// fresh <see cref="IServiceCollection"/> seeded with the config-test.yml-overlaid
/// <see cref="IConfiguration"/>:
/// <list type="number">
/// <item>Load assemblies + register logging + bind <c>[ConfigurationProperties]</c>;</item>
/// <item><c>BootstrapServices</c> (service + portal scan, transport registration);</item>
/// <item>Build the provider, run <c>[PostConstruct]</c> + <c>[AltruistModuleLoader]</c>;</item>
/// <item><c>AltruistStartupConfiguration.BuildAndStartAsync</c> — non-blocking;
///       returns once Kestrel + transports are bound.</item>
/// </list></para>
/// </summary>
internal static class LiveServerHandle
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static WebApplication? _app;
    private static IServiceProvider? _provider;
    private static IDisposable? _scope;

    public static IServiceProvider Provider
    {
        get
        {
            if (_provider is null)
                throw new InvalidOperationException("Live server not started. Call EnsureStartedAsync first.");
            return _provider;
        }
    }

    public static async Task EnsureStartedAsync()
    {
        if (_app is not null) return;

        await _gate.WaitAsync();
        try
        {
            if (_app is not null) return;

            var cfg = AltruistTestRuntime.LoadConfigWithOverlayPublic();
            AppConfigLoader.Set(cfg);

            var services = new ServiceCollection();
            AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
            services.AddSingleton(cfg);
            // Quiet logging by default — most tests treat the server as a black box and
            // don't want startup noise. Override via config-test.yml if you need it.
            services.AddLogging(b => b.ClearProviders().AddProvider(NullLoggerProvider.Instance));
            Dependencies.UseServices(services);

            using (var tmp = services.BuildServiceProvider())
            {
                var log = tmp.GetRequiredService<ILoggerFactory>().CreateLogger("Altruist.Testing.LiveServer");
                AltruistDI.BindConfigurationClasses(services, cfg, log);
                // AltruistServiceConfig (full framework — discovers Portals + transports too,
                // unlike AltruistDI.BootstrapServices which is DI-only).
                await new AltruistServiceConfig(log).Configure(services);
                await new ConfigAttributeConfiguration().Configure(services);
            }

            _provider = services.BuildServiceProvider();
            Dependencies.UseRootProvider(_provider);
            _scope = Dependencies.PushScope(_provider);

            await AltruistDI.RunPostConstructsAsync(_provider, services);
            await AltruistModuleConfig.RunModulesAsync(_provider);

            var startup = _provider.GetService<AltruistStartupConfiguration>()
                ?? throw new InvalidOperationException(
                    "AltruistStartupConfiguration not registered — the live server requires the full framework boot path.");

            _app = await startup.BuildAndStartAsync(services)
                ?? throw new InvalidOperationException(
                    "BuildAndStartAsync returned null — check altruist:server:http:host and :port in config.yml/config-test.yml.");

            // Best-effort cleanup at process exit. xUnit's framework lifecycle doesn't
            // give us a clean assembly-finished hook here, so we hang off ProcessExit.
            AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
            {
                try { StopAsync().GetAwaiter().GetResult(); } catch { }
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is not null)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _app.StopAsync(stopCts.Token); } catch { }
                try { await _app.DisposeAsync(); } catch { }
                _app = null;
            }
            _scope?.Dispose();
            _scope = null;
            (_provider as IDisposable)?.Dispose();
            _provider = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
