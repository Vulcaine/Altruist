/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist;
using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Physx
{
    /// <summary>
    /// Startup configuration (picked up automatically via <c>[ServiceConfiguration]</c>) that discovers and wires
    /// up collision handlers. You normally never call it yourself.
    /// </summary>
    /// <remarks>
    /// Steps performed by <see cref="Configure"/>:
    /// <list type="number">
    /// <item>Scans all loaded, non-dynamic assemblies for classes marked <see cref="CollisionHandlerAttribute"/>.</item>
    /// <item>Skips types rejected by <c>[ConditionalOnConfig]</c>/<c>[ConditionalOnMissingService]</c>, ensures their
    /// constructor dependencies are registered, and registers each handler type as a DI <b>singleton</b>.</item>
    /// <item>Registers an internal bootstrap singleton whose <c>[PostConstruct]</c> step runs once the root provider
    /// is built: it clears <see cref="CollisionHandlerRegistry"/>, resolves each handler from the root provider and
    /// registers its <see cref="CollisionEventAttribute"/> methods via
    /// <see cref="CollisionHandlerDiscovery.RegisterCollisionHandlerTypes"/>.</item>
    /// </list>
    /// No dedicated appsettings keys are read; the loaded configuration is only used to evaluate conditional
    /// attributes on the handler classes. The logger comes from the injected <see cref="ILoggerFactory"/>; no service
    /// provider is built while configuring.
    /// </remarks>
    [ServiceConfiguration]
    public sealed class AltruistCollisionHandlerConfig : IAltruistConfiguration
    {
        /// <inheritdoc/>
        public bool IsConfigured { get; set; }

        private readonly ILogger _logger;

        /// <summary>Created by the framework's configuration bootstrap.</summary>
        /// <param name="loggerFactory">Logger factory for discovery diagnostics.</param>
        /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
        public AltruistCollisionHandlerConfig(ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _logger = loggerFactory.CreateLogger<AltruistCollisionHandlerConfig>();
        }

        /// <summary>Discovers <see cref="CollisionHandlerAttribute"/> classes and registers them (and the registry bootstrap) in <paramref name="services"/>.</summary>
        /// <param name="services">Service collection being configured.</param>
        /// <returns>A completed task; the work is synchronous.</returns>
        public Task Configure(IServiceCollection services)
        {
            var cfg = GetConfig();

            var assemblies = GetAssemblies();

            // 1) Discover all [CollisionHandler] types
            var handlerTypes = TypeDiscovery
                .FindTypesWithAttribute<CollisionHandlerAttribute>(assemblies)
                .ToArray();

            if (handlerTypes.Length == 0)
            {
                _logger.LogDebug("🧩 No [CollisionHandler] classes discovered. Skipping collision handler wiring.");
                IsConfigured = true;
                return Task.CompletedTask;
            }

            _logger.LogDebug("🧩 Discovered {Count} [CollisionHandler] types.", handlerTypes.Length);

            // 2) Register handler types into DI so their constructors can be autowired
            //    We mirror the portal registration pattern:
            //    - Respect ConditionalOnConfig / ShouldRegister
            //    - Plan dependencies via DependencyPlanner
            //    - Use DependencyResolver.CreateWithConfiguration for construction.
            var registered = new List<Type>();

            foreach (var handlerType in handlerTypes)
            {
                if (!DependencyResolver.ShouldRegister(handlerType, cfg, _logger))
                    continue;

                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, _logger, handlerType);

                services.AddSingleton(
                    handlerType,
                    sp => DependencyResolver.CreateWithConfiguration(sp, cfg, handlerType, _logger)!);

                registered.Add(handlerType);
                _logger.LogDebug("🧩 Registered collision handler type {HandlerType} as Singleton.", handlerType.FullName);
            }

            if (registered.Count == 0)
            {
                _logger.LogDebug("🧩 No [CollisionHandler] types passed ConditionalOnConfig / ShouldRegister.");
                IsConfigured = true;
                return Task.CompletedTask;
            }

            services.AddSingleton(new CollisionHandlerBootstrapState(registered.ToArray()));
            services.AddSingleton<CollisionHandlerBootstrap>();

            _logger.LogDebug("✅ Collision handler discovery & registration complete. {Count} handler types wired.",
                registered.Count);

            IsConfigured = true;
            return Task.CompletedTask;
        }

        // ---------- Helpers (copying the style from AltruistServiceConfig) ----------

        private static IConfiguration GetConfig() => AppConfigLoader.Load();

        private static Assembly[] GetAssemblies() =>
            AppDomain.CurrentDomain
                     .GetAssemblies()
                     .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                     .ToArray();
    }

    internal sealed record CollisionHandlerBootstrapState(Type[] HandlerTypes);

    internal sealed class CollisionHandlerBootstrap
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly CollisionHandlerBootstrapState _state;
        private readonly ILogger _logger;

        public CollisionHandlerBootstrap(
            IServiceProvider serviceProvider,
            CollisionHandlerBootstrapState state,
            ILoggerFactory loggerFactory)
        {
            _serviceProvider = serviceProvider;
            _state = state;
            _logger = loggerFactory.CreateLogger<CollisionHandlerBootstrap>();
        }

        [PostConstruct]
        public void RegisterHandlers()
        {
            CollisionHandlerRegistry.Clear();
            CollisionHandlerDiscovery.RegisterCollisionHandlerTypes(
                _state.HandlerTypes,
                type => _serviceProvider.GetService(type),
                _logger);

            _logger.LogInformation(
                "Registered {Count} collision handler methods from the root provider.",
                CollisionHandlerRegistry.TotalHandlerCount);
        }
    }
}
