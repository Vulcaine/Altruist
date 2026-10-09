/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Testing;

/// <summary>
/// Process-wide root for test-time Altruist DI.
///
/// <para><b>Boot.</b> Loads <c>config.yml</c> via the standard <see cref="AppConfigLoader"/>,
/// then optionally overlays <c>config-test.yml</c> from the test assembly's base directory
/// for any test-only overrides (logging, alternate connection strings, etc.). Bootstraps
/// the full Altruist DI container — assembly scan, <c>[ConfigurationProperties]</c> bind,
/// service registration, BuildServiceProvider, <c>[PostConstruct]</c>. <c>[AltruistModuleLoader]</c>
/// runs lazily via <see cref="EnsureModulesRun"/> on first <c>[AltruistTest(RunModuleLoaders = true)]</c>.</para>
///
/// <para><b>Database isolation.</b> The configured persistence provider is used as-is —
/// no automatic database rewrite. Per-class schema isolation lives in
/// <c>AltruistClassRunner</c>: each test class gets its own schema
/// (<c>test_&lt;classname&gt;</c>), purged-then-recreated at class start,
/// dropped at class end (unless <c>[AltruistTest(KeepSchema = true)]</c>).
/// Point <c>config.yml</c> at a test-suitable database; root bootstrap
/// will create the production-named schemas there which serve as template
/// structures for the per-class table clones.</para>
/// </summary>
public static class AltruistTestRuntime
{
    /// <summary>Optional overlay file the runtime looks for next to the test assembly.</summary>
    public const string TestConfigFileName = "config-test.yml";

    private static readonly object _gate = new();
    private static IServiceProvider? _root;
    private static IReadOnlyList<ServiceDescriptor>? _rootDescriptors;

    /// <summary>
    /// Snapshot of the root <see cref="ServiceDescriptor"/>s, in registration order.
    /// Per-class child containers copy these and prepend overrides before
    /// <see cref="ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)"/>.
    /// </summary>
    public static IReadOnlyList<ServiceDescriptor> RootDescriptors
    {
        get
        {
            EnsureBuilt();
            return _rootDescriptors!;
        }
    }

    /// <summary>The shared root <see cref="IServiceProvider"/>. Use sparingly — most tests should resolve via ctor injection.</summary>
    public static IServiceProvider Root
    {
        get
        {
            EnsureBuilt();
            return _root!;
        }
    }

    private static void EnsureBuilt()
    {
        if (_root is not null) return;
        lock (_gate)
        {
            if (_root is not null) return;

            var cfg = LoadConfigWithOverlay();
            AppConfigLoader.Set(cfg);

            var services = new ServiceCollection();
            AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
            services.AddSingleton(cfg);
            services.AddLogging(b => b.ClearProviders().AddProvider(NullLoggerProvider.Instance));
            Dependencies.UseServices(services);

            using (var tmp = services.BuildServiceProvider())
            {
                var log = tmp.GetRequiredService<ILoggerFactory>().CreateLogger("Altruist.Testing");
                AltruistDI.BindConfigurationClasses(services, cfg, log);
                AltruistDI.BootstrapServices(services, log).GetAwaiter().GetResult();
            }

            _rootDescriptors = services.ToList();

            var provider = services.BuildServiceProvider();
            Dependencies.UseRootProvider(provider);
            AltruistDI.RunPostConstructsAsync(provider, services).GetAwaiter().GetResult();
            _root = provider;
        }
    }

    /// <summary>
    /// Same as <see cref="LoadConfigWithOverlay"/>, exposed so the live-server bootstrap
    /// (<c>[AltruistIntegrationTest]</c>) can use the identical config-resolution path
    /// without rolling its own loader.
    /// </summary>
    public static IConfiguration LoadConfigWithOverlayPublic() => LoadConfigWithOverlay();

    /// <summary>
    /// Load <c>config.yml</c>, then layer <c>config-test.yml</c> on top if it exists in the
    /// test assembly's base directory. Test-only overrides (logging level, alternate
    /// database name, etc.) live in the overlay file.
    /// </summary>
    private static IConfiguration LoadConfigWithOverlay()
    {
        var baseCfg = AppConfigLoader.Load();
        var testYamlPath = Path.Combine(AppContext.BaseDirectory, TestConfigFileName);

        if (!File.Exists(testYamlPath))
            return baseCfg;

        return new ConfigurationBuilder()
            .AddConfiguration(baseCfg)
            .AddYamlFile(testYamlPath, optional: true, reloadOnChange: false)
            .Build();
    }

    private static int _modulesRun;
    /// <summary>
    /// Optionally invokes <c>[AltruistModuleLoader]</c> static methods after the root
    /// is built. Called from the class runner when an
    /// <c>[AltruistTest(RunModuleLoaders = true)]</c> class is first encountered.
    /// Idempotent.
    /// </summary>
    public static void EnsureModulesRun()
    {
        EnsureBuilt();
        if (Interlocked.Exchange(ref _modulesRun, 1) == 1) return;
        AltruistModuleConfig.RunModulesAsync(_root!).GetAwaiter().GetResult();
    }
}
