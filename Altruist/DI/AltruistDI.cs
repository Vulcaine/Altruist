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
/// </summary>
/// <remarks>
/// <para>Use <see cref="Run"/> for a console tool / worker that only wants the attribute-driven container. For a server
/// (HTTP, transports, portals, modules) use <c>AltruistApplication.Run</c> instead, which shares
/// <see cref="Services"/> and the pieces below (<see cref="BindConfigurationClasses"/>, PostConstruct handling) but
/// registers services through the full framework configuration.</para>
/// <para>After <see cref="Run"/>, resolve services with <see cref="Dependencies.Inject{T}"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// public static async Task Main(string[] args)
/// {
///     await AltruistDI.Run(args);
///     var jobs = Dependencies.Inject&lt;IJobRunner&gt;();
///     await jobs.RunAllAsync();
/// }
/// </code>
/// </example>
public static class AltruistDI
{
    /// <summary>The process-wide service collection all bootstrap steps register into. Add manual registrations here before calling <see cref="Run"/>.</summary>
    public static readonly IServiceCollection Services = new ServiceCollection();

    private static readonly HashSet<string> _constructionCache = new();

    /// <summary>
    /// Boots the DI container: loads configuration (<see cref="AppConfigLoader.Load"/>), loads all referenced assemblies,
    /// configures console logging (<c>altruist:logging:console</c>), binds <see cref="ConfigurationPropertiesAttribute"/>
    /// classes, registers beans, <see cref="ServiceAttribute"/> classes and <see cref="ServiceConfigurationAttribute"/> steps,
    /// builds the root provider (published via <see cref="Dependencies.UseRootProvider"/>) and runs
    /// <see cref="PostConstructAttribute"/> hooks. Call once per process.
    /// </summary>
    /// <param name="args">Command-line arguments, added as the highest-priority configuration source (only if configuration was not loaded yet).</param>
    public static async Task Run(string[]? args = null)
    {
        var cfg = AppConfigLoader.Load(args);

#if !NETSTANDARD2_1
        AssemblyLoader.EnsureAllReferencedAssembliesLoaded();
#endif
        ConfigureLogging(Services, cfg);

        Services.AddSingleton(cfg);

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

    /// <summary>
    /// Registers bean methods and <see cref="ServiceAttribute"/> classes (<see cref="AltruistDIServiceConfig"/>), then discovers
    /// and runs <see cref="ServiceConfigurationAttribute"/> steps (<see cref="ConfigAttributeConfiguration"/>) on <paramref name="services"/>.
    /// Use this when you own the <see cref="IServiceCollection"/> (e.g. tests or a custom host) instead of <see cref="Run"/>.
    /// The collection must already contain logging.
    /// </summary>
    /// <param name="services">Collection to register into.</param>
    /// <param name="log">Logger for registration messages; created from <paramref name="services"/> when null.</param>
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

    /// <summary>
    /// Adds logging with the console provider, unless <c>altruist:logging:console</c> is <c>false</c> or <c>0</c>
    /// (then no provider is added). Clears any previously added providers.
    /// </summary>
    /// <param name="services">Collection to configure.</param>
    /// <param name="cfg">Configuration to read the switch from; null keeps the console on.</param>
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

    /// <summary>
    /// Binds every class marked with <see cref="ConfigurationPropertiesAttribute"/> to its section and registers the result
    /// as a singleton (list sections as <c>List&lt;T&gt;</c> / <c>IEnumerable&lt;T&gt;</c> / <c>IReadOnlyList&lt;T&gt;</c>).
    /// Missing sections are skipped (the type is then not registered).
    /// </summary>
    /// <param name="services">Collection to register into.</param>
    /// <param name="cfg">Configuration root.</param>
    /// <param name="logger">Logger for diagnostics.</param>
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

    /// <summary>
    /// Resolves every distinct service type in <paramref name="services"/> from <paramref name="provider"/> (which eagerly
    /// creates singletons) and invokes its <see cref="PostConstructAttribute"/> method, once per implementation type per
    /// process. Resolution failures and resolutions taking over 10 s are written to stderr and skipped, not thrown; an
    /// exception thrown by a hook propagates.
    /// </summary>
    /// <param name="provider">The built root provider.</param>
    /// <param name="services">The collection the provider was built from.</param>
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
