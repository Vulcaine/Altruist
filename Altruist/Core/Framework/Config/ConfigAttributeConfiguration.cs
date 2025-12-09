// Altruist/ConfigAttributeConfiguration.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy of the License at
http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public sealed class ConfigAttributeConfiguration : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        public async Task Configure(IServiceCollection services)
        {
            // Make sure IServiceCollection itself is injectable
            if (!services.Any(d => d.ServiceType == typeof(IServiceCollection)))
                services.AddSingleton(services);

            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var candidates = TypeDiscovery
                .FindTypesWithAttribute<ServiceConfigurationAttribute>(assemblies)
                .Select(t => new { Type = t, Attr = t.GetCustomAttribute<ServiceConfigurationAttribute>()! })
                .OrderBy(x => x.Attr.Order)
                .ThenBy(x => x.Type.FullName)
                .ToArray();

            var cfg = AppConfigLoader.Load();

            using var tmp = services.BuildServiceProvider();
            var loggerFactory = tmp.GetRequiredService<ILoggerFactory>();
            var bootstrapLogger = loggerFactory.CreateLogger<ConfigAttributeConfiguration>();

            DependencyResolver.EnsureConverters(services, cfg, bootstrapLogger);

            // 1) Register all configuration classes in DI (once)
            foreach (var item in candidates)
            {
                if (!typeof(IAltruistConfiguration).IsAssignableFrom(item.Type))
                    continue;

                RegisterConfigType(services, cfg, item.Type, item.Attr, bootstrapLogger);
            }

            // 2) Execute Configure() on each configuration instance (once)
            foreach (var item in candidates)
            {
                if (!typeof(IAltruistConfiguration).IsAssignableFrom(item.Type))
                {
                    bootstrapLogger.LogError(
                        "{Type} is marked with [ServiceConfiguration] but does not implement IAltruistConfiguration. Skipping.",
                        item.Type.FullName);
                    continue;
                }

                await ConfigureConfigTypeAsync(services, cfg, item.Type, item.Attr, bootstrapLogger)
                    .ConfigureAwait(false);
            }

            IsConfigured = true;
        }

        /// <summary>
        /// Used by service registration to ensure a given configuration type
        /// is both registered in DI and has had Configure() executed once.
        /// </summary>
        public static void EnsureConfigurationRegisteredAndConfigured(
            IServiceCollection services,
            Type configType,
            IConfiguration cfg,
            ILogger log)
        {
            if (configType is null)
                throw new ArgumentNullException(nameof(configType));

            if (!typeof(IAltruistConfiguration).IsAssignableFrom(configType))
            {
                log.LogWarning(
                    "DependsOn configuration type {ConfigType} does not implement IAltruistConfiguration. Ignoring.",
                    configType.FullName);
                return;
            }

            var attr = configType.GetCustomAttribute<ServiceConfigurationAttribute>()
                       ?? new ServiceConfigurationAttribute(); // default: self, singleton

            // 1) Register the configuration type (if not yet registered)
            RegisterConfigType(services, cfg, configType, attr, log);

            // 2) Configure the instance once
            ConfigureConfigTypeSync(services, cfg, configType, attr, log);
        }

        // ----------------- helpers -----------------

        private static void RegisterConfigType(
            IServiceCollection services,
            IConfiguration cfg,
            Type type,
            ServiceConfigurationAttribute attr,
            ILogger log)
        {
            if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                return;

            if (!DependencyResolver.ShouldRegister(type, cfg, log))
                return;

            var serviceType = attr.ServiceType ?? type;
            var lifetime = attr.Lifetime;

            // Avoid duplicate registrations
            if (services.Any(d => d.ServiceType == serviceType))
                return;

            services.Add(new ServiceDescriptor(
                serviceType,
                sp =>
                {
                    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                    var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, type, logger, lifetime);
                    return instance!;
                },
                lifetime));

            log.LogDebug("🔧 Registered configuration {Config} as {ServiceType} ({Lifetime}).",
                type.FullName, serviceType.FullName, lifetime);
        }

        private static async Task ConfigureConfigTypeAsync(
            IServiceCollection services,
            IConfiguration cfg,
            Type type,
            ServiceConfigurationAttribute attr,
            ILogger log)
        {
            var serviceType = attr.ServiceType ?? type;

            using var sp = services.BuildServiceProvider();
            var instanceObj = sp.GetService(serviceType);
            if (instanceObj is null)
                return;

            if (instanceObj is not IAltruistConfiguration configInstance)
            {
                log.LogError(
                    "Resolved {Service} does not implement IAltruistConfiguration. Skipping.",
                    serviceType.FullName);
                return;
            }

            if (configInstance.IsConfigured)
            {
                log.LogDebug("Skipping Configure for {Type} (already configured).", type.FullName);
                return;
            }

            try
            {
                await configInstance.Configure(services).ConfigureAwait(false);
                configInstance.IsConfigured = true;

                log.LogDebug("Ran Configure for {Type} (Order={Order}).", type.FullName, attr.Order);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                log.LogError(tie.InnerException, "Error running Configure on {Type}.", type.FullName);
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to execute configuration for {Type}.", type.FullName);
                throw;
            }
        }

        /// <summary>
        /// Synchronous wrapper used from DependsOn path.
        /// </summary>
        private static void ConfigureConfigTypeSync(
            IServiceCollection services,
            IConfiguration cfg,
            Type type,
            ServiceConfigurationAttribute attr,
            ILogger log)
        {
            ConfigureConfigTypeAsync(services, cfg, type, attr, log)
                .GetAwaiter()
                .GetResult();
        }
    }
}
