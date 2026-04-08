// Altruist/Bootstrap.cs
using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class AltruistBootstrap
{
    /// <summary>
    /// Shared service collection — delegates to AltruistDI.Services.
    /// </summary>
    public static IServiceCollection Services => AltruistDI.Services;

    private static readonly HashSet<string> _constructionCache = new();

    public static async Task Bootstrap()
    {
        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();

        // Register VaultAttribute as a service marker so DependencyPlanner
        // can auto-register vault entities as dependencies
        DependencyPlanner.RegisterServiceMarker(typeof(VaultAttribute));

        // Use Altruist-specific logger instead of DI's default console logger
        ConfigureLogging();
        Dependencies.UseServices(Services);

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
        await RunPostConstructsAsync(provider);
        Console.Error.WriteLine("[BOOTSTRAP] Running Modules...");
        await AltruistModuleConfig.RunModulesAsync(provider);
        Console.Error.WriteLine("[BOOTSTRAP] Starting HTTP...");

        var startup = provider.GetService<AltruistStartupConfiguration>();
        if (startup is not null)
            await startup.StartAsync(Services);
    }

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

    public static void ConfigureLogging()
    {
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";
            loggingBuilder.AddProvider(new AltruistLoggerProvider(frameworkVersion));
        });
    }

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
                // Detect circular dependency / deadlock with timeout
                var resolveTask = Task.Run(() => provider.GetService(serviceType));
                if (!resolveTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    Console.Error.WriteLine(
                        $"[ALTRUIST] Circular dependency detected: resolving '{serviceType.Name}' " +
                        $"timed out after 10s. Check constructor dependencies for cycles. Skipping.");
                    continue;
                }
                instance = resolveTask.Result;
            }
            catch (AggregateException ae) when (ae.InnerException is not null)
            {
                Console.Error.WriteLine($"[ALTRUIST] Failed to resolve '{serviceType.Name}': {ae.InnerException.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ALTRUIST] Failed to resolve '{serviceType.Name}': {ex.Message}");
                continue;
            }

            if (instance is null)
                continue;

            var implType = instance.GetType();
            if (!_constructionCache.Add(implType.FullName!))
                continue;

            if (!HasPostConstruct(implType))
                continue;

            Console.WriteLine($"[POSTCONSTRUCT] {implType.Name}...");
            var log = loggerFactory.CreateLogger(implType);
            await DependencyResolver.InvokePostConstructAsync(instance, provider, cfg, log);
            Console.WriteLine($"[POSTCONSTRUCT] {implType.Name} done.");
        }
    }

    private static bool HasPostConstruct(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.GetCustomAttribute<PostConstructAttribute>(inherit: true) is not null);
}
