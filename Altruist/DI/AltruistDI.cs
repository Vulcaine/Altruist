/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// Standalone DI entry point. Scans assemblies for [Service] attributes,
/// resolves dependencies, binds config values, and runs [PostConstruct] hooks.
/// No server, no networking, no gaming — just DI.
///
/// Usage:
///   await AltruistDI.Run(args);
///
/// The full framework (AltruistApplication.Run) delegates to this internally
/// and adds Portal/Module/Transport on top.
/// </summary>
public static class AltruistDI
{
    public static readonly IServiceCollection Services = new ServiceCollection();

    private static readonly HashSet<string> _constructionCache = new();

    public static async Task Run(string[]? args = null)
    {
        var cfg = AppConfigLoader.Load(args);

        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
        ConfigureLogging(Services, cfg);

        Services.AddSingleton(cfg);
        Dependencies.UseServices(Services);

        using var tmpProvider = Services.BuildServiceProvider();
        var logger = tmpProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<AltruistDIServiceConfig>();

        BindConfigurationClasses(Services, cfg, logger);

        await BootstrapServices(Services, logger);

        var provider = Services.BuildServiceProvider();
        Dependencies.UseRootProvider(provider);
        await RunPostConstructsAsync(provider, Services);
    }

    public static async Task BootstrapServices(IServiceCollection services, ILogger? log = null)
    {
        if (log is null)
        {
            using var tmpProvider = services.BuildServiceProvider();
            log = tmpProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<AltruistDIServiceConfig>();
        }

        var cfg = AppConfigLoader.Load();
        await new AltruistDIServiceConfig(log).Configure(services);
        await new ConfigAttributeConfiguration().Configure(services);
    }

    public static void ConfigureLogging(IServiceCollection services, IConfiguration? cfg = null)
    {
        bool consoleEnabled = true;
        if (cfg != null)
        {
            var val = cfg["altruist:logging:console"];
            if (val != null && (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0"))
                consoleEnabled = false;
        }

        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            if (consoleEnabled)
                loggingBuilder.AddConsole();
        });
    }

    public static void BindConfigurationClasses(IServiceCollection services, IConfiguration cfg, ILogger logger)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        var cfgTypes = assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<ConfigurationPropertiesAttribute>(false) is not null)
            .ToArray();

        if (cfgTypes.Length == 0)
        {
            logger.LogDebug("No classes annotated with [ConfigurationProperties] found.");
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
                logger.LogDebug("Bound config array '{Path}' to {Type}.", attr.Path, listType.Name);
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
                logger.LogDebug("Bound config '{Path}' to {Type}.", attr.Path, t.Name);
            }
        }
    }

    public static async Task RunPostConstructsAsync(IServiceProvider provider, IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var processedServiceTypes = new HashSet<Type>();

        foreach (var descriptor in services)
        {
            var serviceType = descriptor.ServiceType;
            if (serviceType is null)
                continue;

            if (!processedServiceTypes.Add(serviceType))
                continue;

            object? instance;
            try
            {
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

            var log = loggerFactory.CreateLogger(implType);
            await DependencyResolver.InvokePostConstructAsync(instance, provider, cfg, log);
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
