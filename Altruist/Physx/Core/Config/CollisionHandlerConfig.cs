/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Physx
{
    /// <summary>
    /// Configuration that discovers and wires up collision handlers.
    ///
    /// It behaves like a "service bootstrapper":
    ///  - Discovers all classes marked with [CollisionHandler]
    ///  - Registers them in DI so their constructors are autowired
    ///  - Builds a temporary ServiceProvider and instantiates them
    ///  - Uses CollisionHandlerDiscovery to find [CollisionEvent] methods
    ///    and register them into CollisionHandlerRegistry.
    /// </summary>
    [ServiceConfiguration]
    public sealed class AltruistCollisionHandlerConfig : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        public Task Configure(IServiceCollection services)
        {
            var cfg = GetConfig();
            var logger = GetLogger(services);

            var assemblies = GetAssemblies();

            // 1) Discover all [CollisionHandler] types
            var handlerTypes = TypeDiscovery
                .FindTypesWithAttribute<CollisionHandlerAttribute>(assemblies)
                .ToArray();

            if (handlerTypes.Length == 0)
            {
                logger.LogDebug("🧩 No [CollisionHandler] classes discovered. Skipping collision handler wiring.");
                IsConfigured = true;
                return Task.CompletedTask;
            }

            logger.LogDebug("🧩 Discovered {Count} [CollisionHandler] types.", handlerTypes.Length);

            // 2) Register handler types into DI so their constructors can be autowired
            //    We mirror the portal registration pattern:
            //    - Respect ConditionalOnConfig / ShouldRegister
            //    - Plan dependencies via DependencyPlanner
            //    - Use DependencyResolver.CreateWithConfiguration for construction.
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
                logger.LogDebug("🧩 Registered collision handler type {HandlerType} as Singleton.", handlerType.FullName);
            }

            if (registered.Count == 0)
            {
                logger.LogDebug("🧩 No [CollisionHandler] types passed ConditionalOnConfig / ShouldRegister.");
                IsConfigured = true;
                return Task.CompletedTask;
            }

            using (var warmupProvider = services.BuildServiceProvider())
            {
                CollisionHandlerDiscovery.RegisterCollisionHandlers(
                    assemblies,
                    type => warmupProvider.GetService(type),
                    logger);
            }

            logger.LogDebug("✅ Collision handler discovery & registration complete. {Count} handler types wired.",
                registered.Count);

            IsConfigured = true;
            return Task.CompletedTask;
        }

        // ---------- Helpers (copying the style from AltruistServiceConfig) ----------

        private static IConfiguration GetConfig() => AppConfigLoader.Load();

        private static ILogger GetLogger(IServiceCollection services) =>
            services.BuildServiceProvider()
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger<AltruistCollisionHandlerConfig>();

        private static Assembly[] GetAssemblies() =>
            AppDomain.CurrentDomain
                     .GetAssemblies()
                     .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                     .ToArray();
    }
}
