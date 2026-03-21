// Altruist/Bootstrap.cs
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class AltruistBootstrap
{
    public static readonly IServiceCollection Services = new ServiceCollection();

    private static readonly HashSet<string> _constructionCache = new();

    public static async Task Bootstrap()
    {
        EnsureAltruistAssembliesLoaded();
        ConfigureLogging();

        using var tmpProvider = Services.BuildServiceProvider();
        var logger = tmpProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AltruistModuleConfig>();

        BindConfigurationClasses(Services, logger);
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

    private static void EnsureAltruistAssembliesLoaded()
    {
        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
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
                        $"[ALTRUIST] ⚠️ Circular dependency detected: resolving '{serviceType.Name}' " +
                        $"timed out after 10s. Check constructor dependencies for cycles. Skipping.");
                    continue;
                }
                instance = resolveTask.Result;
            }
            catch (AggregateException ae) when (ae.InnerException is not null)
            {
                Console.Error.WriteLine($"[ALTRUIST] ❌ Failed to resolve '{serviceType.Name}': {ae.InnerException.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ALTRUIST] ❌ Failed to resolve '{serviceType.Name}': {ex.Message}");
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

    private static void BindConfigurationClasses(IServiceCollection services, ILogger logger)
    {
        var cfg = AppConfigLoader.Load();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        var cfgTypes = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<ConfigurationPropertiesAttribute>(false) is not null)
            .ToArray();

        if (cfgTypes.Length == 0)
        {
            logger.LogDebug("ℹ️ No classes annotated with [ConfigurationProperties] found.");
            return;
        }

        foreach (var t in cfgTypes)
        {
            var attr = t.GetCustomAttribute<ConfigurationPropertiesAttribute>(false)!;
            var section = cfg.GetSection(attr.Path);

            if (!section.Exists())
            {
                logger.LogDebug("Config section '{Path}' not found for type {Type}. Skipping.", attr.Path, t.FullName);
                continue;
            }

            var looksArray = section.GetChildren().Any(c => int.TryParse(c.Key, out _));
            if (looksArray)
            {
                var listType = typeof(List<>).MakeGenericType(t);
                var listInstance = section.Get(listType);
                if (listInstance is null)
                {
                    logger.LogWarning("Failed to bind list at '{Path}' to {Type}.", attr.Path, listType);
                    continue;
                }

                services.AddSingleton(listType, listInstance);
                var ienumType = typeof(IEnumerable<>).MakeGenericType(t);
                var ireadOnlyListType = typeof(IReadOnlyList<>).MakeGenericType(t);
                services.AddSingleton(ienumType, sp => sp.GetRequiredService(listType));
                services.AddSingleton(ireadOnlyListType, sp => sp.GetRequiredService(listType));
                logger.LogDebug("🔧 Bound config array '{Path}' to {Type}.", attr.Path, listType.Name);
            }
            else
            {
                var instance = Activator.CreateInstance(t);
                if (instance is null)
                {
                    logger.LogWarning("Failed to create instance of {Type} for binding.", t.FullName);
                    continue;
                }

                section.Bind(instance);
                services.AddSingleton(t, instance);
                logger.LogDebug("🔧 Bound config '{Path}' to {Type}.", attr.Path, t.Name);
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static bool HasPostConstruct(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.GetCustomAttribute<PostConstructAttribute>(inherit: true) is not null);
}
