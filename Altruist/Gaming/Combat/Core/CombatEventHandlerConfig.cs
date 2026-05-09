using System.Reflection;
using Altruist;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

[ServiceConfiguration]
public sealed class CombatEventHandlerConfig : IAltruistConfiguration
{
    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var logger = services.BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<CombatEventHandlerConfig>();

        var assemblies = GetAssemblies();
        var handlerTypes = TypeDiscovery
            .FindTypesWithAttribute<CombatHandlerAttribute>(assemblies)
            .ToArray();

        if (handlerTypes.Length == 0)
        {
            logger.LogDebug("No [CombatHandler] classes discovered. Skipping combat handler wiring.");
            IsConfigured = true;
            return Task.CompletedTask;
        }

        var registered = new List<Type>();
        foreach (var handlerType in handlerTypes)
        {
            if (!DependencyResolver.ShouldRegister(handlerType, cfg, logger))
                continue;

            DependencyPlanner.EnsureDependenciesRegistered(services, cfg, logger, handlerType);

            services.AddSingleton(
                handlerType,
                sp => DependencyResolver.CreateWithConfiguration(sp, cfg, handlerType, logger)!);

            registered.Add(handlerType);
        }

        if (registered.Count == 0)
        {
            IsConfigured = true;
            return Task.CompletedTask;
        }

        // Combat handler INSTANCE wiring is deferred to CombatHandlerInitializer's
        // [PostConstruct] — running it here against a throwaway BuildServiceProvider()
        // would resolve handlers with isolated singleton dependencies (their own
        // disconnected IConnectionStore / IAltruistRouter), and once that warmup
        // provider is disposed the handlers continue to live with dead deps —
        // every send via `_router.Client.SendAsync(...)` then silently no-ops
        // because the warmup-isolated connection store is empty. PostConstruct
        // resolves against the live root container, so handlers share singletons
        // with the rest of the app.
        services.AddSingleton<CombatHandlerInitializer>();

        logger.LogDebug("Combat event handler types registered. {Count} handler types wired (instance discovery deferred to PostConstruct).", registered.Count);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    private static Assembly[] GetAssemblies() =>
        AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();
}
