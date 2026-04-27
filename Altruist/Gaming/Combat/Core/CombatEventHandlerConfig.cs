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

        using (var warmupProvider = services.BuildServiceProvider())
        {
            CombatEventHandlerDiscovery.RegisterCombatHandlers(
                assemblies,
                type => warmupProvider.GetService(type),
                logger);
        }

        logger.LogDebug("Combat event handler discovery complete. {Count} handler types wired.", registered.Count);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    private static Assembly[] GetAssemblies() =>
        AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();
}
