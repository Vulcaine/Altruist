/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Reflection;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    /// <summary>
    /// Discovers all types marked with <see cref="ConfigurationAttribute"/>,
    /// registers each as a service according to the attribute (ServiceType/Lifetime),
    /// then executes them in ascending <c>Order</c>.
    /// 
    /// Instances are created via <see cref="DependencyResolver.CreateWithConfiguration"/> and
    /// <see cref="DependencyResolver.InvokePostConstruct"/> to honor [ConfigValue] and [PostConstruct].
    /// Types that do not implement <see cref="IAltruistConfiguration"/> are skipped with an error log.
    /// </summary>
    public sealed class ConfigAttributeConfiguration : IAltruistConfiguration
    {
        public async Task Configure(IServiceCollection services)
        {
            // Ensure IServiceCollection itself is injectable (some configs want it in ctor)
            if (!services.Any(d => d.ServiceType == typeof(IServiceCollection)))
                services.AddSingleton<IServiceCollection>(services);

            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var candidates = TypeDiscovery
                .FindTypesWithAttribute<ConfigurationAttribute>(assemblies)
                .Select(t => new { Type = t, Attr = t.GetCustomAttribute<ConfigurationAttribute>()! })
                .OrderBy(x => x.Attr.Order)
                .ThenBy(x => x.Type.FullName)
                .ToArray();

            // Load config once for ShouldRegister checks and construction
            var cfg = AppConfigLoader.Load();

            // Phase 1: Register each configuration type as a service according to its attribute
            foreach (var item in candidates)
            {
                var type = item.Type;
                var attr = item.Attr;

                // Only configs are valid
                if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                    continue;

                // Respect ConditionalOnConfig on the configuration class (if any)
                // Skip registration entirely if condition fails.
                using (var tmp = services.BuildServiceProvider())
                {
                    var log = tmp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                    if (!DependencyResolver.ShouldRegister(type, cfg, log))
                        continue;

                    var serviceType = attr.ServiceType ?? type;
                    var lifetime = attr.Lifetime;

                    // Register using the same pattern as service registration:
                    // construct via DependencyResolver + PostConstruct.
                    services.Add(new ServiceDescriptor(
                        serviceType,
                        sp =>
                        {
                            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                            var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, type, logger, attr.Lifetime);
                            try
                            {
                                _ = DependencyResolver.InvokePostConstructAsync(instance, sp, cfg, logger);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "❌ PostConstruct failed on configuration {Type}.", type.FullName);
                                throw;
                            }
                            return instance!;
                        },
                        lifetime));
                }
            }

            // Phase 2: Execute them in order (build a provider so newly added registrations are resolvable)
            foreach (var item in candidates)
            {
                using var sp = services.BuildServiceProvider();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();

                var type = item.Type;
                var attr = item.Attr;

                if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                {
                    logger.LogError("⚠️ {Type} is marked with [Configuration] but does not implement IAltruistConfiguration. Skipping.", type.FullName);
                    continue;
                }

                // If ShouldRegister failed earlier, the service won't be in the container; skip quietly.
                object? instanceObj = null;
                try
                {
                    var serviceType = attr.ServiceType ?? type;

                    // Try to resolve the registered service; if not present, skip.
                    instanceObj = sp.GetService(serviceType);
                    if (instanceObj is null)
                        continue;

                    // Make sure we have the IAltruistConfiguration instance
                    if (instanceObj is not IAltruistConfiguration configInstance)
                    {
                        logger.LogError("⚠️ Resolved {Service} does not implement IAltruistConfiguration. Skipping.", serviceType.FullName);
                        continue;
                    }

                    await configInstance.Configure(services).ConfigureAwait(false);
                    logger.LogDebug("✅ Ran Configure for {Type} (Order={Order}).", type.FullName, attr.Order);
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    logger.LogError(tie.InnerException, "❌ Error running Configure on {Type}.", type.FullName);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Failed to execute configuration for {Type}.", type.FullName);
                    throw;
                }
            }
        }
    }
}
