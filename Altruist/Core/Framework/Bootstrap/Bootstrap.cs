// Altruist/Bootstrap.cs
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class AltruistBootstrap
{
    public static readonly IServiceCollection Services = new ServiceCollection();

    private static readonly HashSet<string> _constructionCache = new();

    /// <summary>
    /// Entry point called by your app. Add more steps after BootstrapServices as needed.
    /// </summary>
    public static async Task Bootstrap()
    {
        EnsureAltruistAssembliesLoaded();
        ConfigureLogging();
        await BootstrapModules();
        await BootstrapServices();

        // Build a single root provider
        using var provider = Services.BuildServiceProvider();

        // 1) Run global [PostConstruct] for all services
        await RunPostConstructsAsync(provider);

        // 2) Start the HTTP server (if AltruistStartupConfiguration is registered)
        var startup = provider.GetService<AltruistStartupConfiguration>();
        if (startup is not null)
        {
            await startup.StartAsync(Services);
        }
    }

    // If you still need this somewhere else, keep it:
    public static IServiceProvider GetServiceProvider() => Services.BuildServiceProvider();

    /// <summary>
    /// Scans loaded assemblies for types annotated with [Service] and registers them.
    /// Uses a console logger by default, or the app's configured ILoggerFactory if available.
    /// </summary>
    public static async Task BootstrapServices()
    {
        await new AltruistServiceConfig().Configure(Services);
        await new ConfigAttributeConfiguration().Configure(Services);
    }

    public static async Task BootstrapModules()
    {
        await new AltruistModuleConfig().Configure(Services);
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

    /// <summary>
    /// After all services/modules are registered, run [PostConstruct]
    /// for every service whose concrete type declares one.
    /// </summary>
    private static async Task RunPostConstructsAsync(IServiceProvider provider)
    {
        var cfg = AppConfigLoader.Load();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var processedServiceTypes = new HashSet<Type>();

        foreach (var descriptor in Services)
        {
            var serviceType = descriptor.ServiceType;
            if (serviceType is null)
                continue;

            if (!processedServiceTypes.Add(serviceType))
                continue;

            object? instance;
            try
            {
                instance = provider.GetService(serviceType);
            }
            catch
            {
                // If resolution fails here, skip – real errors will surface when used.
                continue;
            }

            if (instance is null)
                continue;

            var implType = instance.GetType();
            if (!_constructionCache.Add(implType.FullName!))
                continue;

            if (!HasPostConstruct(implType))
                continue;

            var log = loggerFactory.CreateLogger(implType);
            await DependencyResolver.InvokePostConstructAsync(instance, provider, cfg, log);
        }
    }

    private static bool HasPostConstruct(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.GetCustomAttribute<PostConstructAttribute>(inherit: true) is not null);
}
