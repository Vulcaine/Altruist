// Altruist/Bootstrap.cs
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class AltruistBootstrap
{
    public static readonly IServiceCollection Services = new ServiceCollection();

    /// <summary>
    /// Entry point called by your app. Add more steps after BootstrapServices as needed.
    /// </summary>
    public static void Bootstrap()
    {
        EnsureAltruistAssembliesLoaded();
        ConfigureLogging();
        BootstrapModules();
        BootstrapServices();
    }

    /// <summary>
    /// Scans loaded assemblies for types annotated with [Service] and registers them.
    /// Uses a console logger by default, or the app's configured ILoggerFactory if available.
    /// </summary>
    public static void BootstrapServices()
    {
        new AltruistServiceConfig().Configure(Services);
    }

    public static void BootstrapModules()
    {
        new AltruistModuleConfig().Configure(Services);
    }

    /// <summary>
    /// Registers a simple console logger if logging hasn't been configured yet.
    /// Safe to call multiple times.
    /// </summary>
    public static void ConfigureLogging()
    {
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";
            loggingBuilder.AddProvider(new AltruistLoggerProvider(frameworkVersion));
        });
    }

    private static void EnsureAltruistAssembliesLoaded()
    {
        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
    }
}
