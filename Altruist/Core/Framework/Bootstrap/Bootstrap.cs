// Altruist/Bootstrap.cs
using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// The server bootstrap sequence behind <see cref="AltruistApplication.Run"/>. Call <see cref="AltruistApplication.Run"/>
/// rather than this class directly; its members are public for custom hosts and tests.
/// </summary>
public static class AltruistBootstrap
{
    /// <summary>
    /// Shared service collection — delegates to AltruistDI.Services.
    /// </summary>
    public static IServiceCollection Services => AltruistDI.Services;

    /// <summary>
    /// Loads all referenced assemblies, configures the Altruist logger, binds <see cref="ConfigurationPropertiesAttribute"/>
    /// classes, registers services (<see cref="BootstrapServices"/>), builds the root provider (published to
    /// <see cref="Dependencies"/>), runs <see cref="PostConstructAttribute"/> hooks and modules, then starts
    /// <see cref="AltruistStartupConfiguration"/> (HTTP + transports) and waits for shutdown.
    /// </summary>
    public static async Task Bootstrap()
    {
        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();

        // Register VaultAttribute as a service marker so DependencyPlanner
        // can auto-register vault entities as dependencies
        DependencyPlanner.RegisterServiceMarker(typeof(VaultAttribute));

        // Use Altruist-specific logger instead of DI's default console logger
        ConfigureLogging();

        var cfg = AppConfigLoader.Load();
        Services.AddSingleton(cfg);

        using var tmpProvider = Services.BuildServiceProvider();
        var logger = tmpProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AltruistModuleConfig>();

        AltruistDI.BindConfigurationClasses(Services, cfg, logger);
        await BootstrapServices();

        using var provider = Services.BuildServiceProvider();
        Dependencies.UseRootProvider(provider);
        await AltruistDI.RunPostConstructsAsync(provider, Services);
        Console.Error.WriteLine("[BOOTSTRAP] Running Modules...");
        await AltruistModuleConfig.RunModulesAsync(provider);
        Console.Error.WriteLine("[BOOTSTRAP] Starting HTTP...");

        var startup = provider.GetService<AltruistStartupConfiguration>();
        if (startup is not null)
            await startup.StartAsync(Services, provider);
    }

    /// <summary>
    /// Registers beans, <see cref="ServiceAttribute"/> classes and portals (<see cref="AltruistServiceConfig"/>), then runs
    /// <see cref="ServiceConfigurationAttribute"/> steps (<see cref="ConfigAttributeConfiguration"/>) on <see cref="Services"/>.
    /// </summary>
    public static async Task BootstrapServices()
    {
        using var tmpProvider = Services.BuildServiceProvider();
        var logger = tmpProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AltruistServiceConfig>();

        // Full framework uses AltruistServiceConfig (with Portal discovery)
        await new AltruistServiceConfig(logger).Configure(Services);
        await new ConfigAttributeConfiguration().Configure(Services);
    }

    /// <summary>Replaces logging providers on <see cref="Services"/> with the Altruist console logger provider.</summary>
    public static void ConfigureLogging()
    {
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";
            loggingBuilder.AddProvider(new AltruistLoggerProvider(frameworkVersion));
        });
    }
}
