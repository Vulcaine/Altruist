// Altruist/ConfigAttributeConfiguration.cs
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
    public sealed class ConfigAttributeConfiguration : IAltruistConfiguration
    {
        public async Task Configure(IServiceCollection services)
        {
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

            using (var tmp = services.BuildServiceProvider())
            {
                var bootstrapLogger = tmp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                DependencyResolver.EnsureConverters(services, bootstrapLogger);
            }

            foreach (var item in candidates)
            {
                var type = item.Type;
                var attr = item.Attr;

                if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                    continue;

                using (var tmp = services.BuildServiceProvider())
                {
                    var log = tmp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                    if (!DependencyResolver.ShouldRegister(type, cfg, log))
                        continue;

                    var serviceType = attr.ServiceType ?? type;
                    var lifetime = attr.Lifetime;

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
                                logger.LogError(ex, "PostConstruct failed on configuration {Type}.", type.FullName);
                                throw;
                            }
                            return instance!;
                        },
                        lifetime));
                }
            }

            foreach (var item in candidates)
            {
                using var sp = services.BuildServiceProvider();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();

                var type = item.Type;
                var attr = item.Attr;

                if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                {
                    logger.LogError("{Type} is marked with [AltruistConfiguration] but does not implement IAltruistConfiguration. Skipping.", type.FullName);
                    continue;
                }

                try
                {
                    var serviceType = attr.ServiceType ?? type;
                    var instanceObj = sp.GetService(serviceType);
                    if (instanceObj is null)
                        continue;

                    if (instanceObj is not IAltruistConfiguration configInstance)
                    {
                        logger.LogError("Resolved {Service} does not implement IAltruistConfiguration. Skipping.", serviceType.FullName);
                        continue;
                    }

                    await configInstance.Configure(services).ConfigureAwait(false);
                    logger.LogDebug("Ran Configure for {Type} (Order={Order}).", type.FullName, attr.Order);
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    logger.LogError(tie.InnerException, "Error running Configure on {Type}.", type.FullName);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to execute configuration for {Type}.", type.FullName);
                    throw;
                }
            }
        }
    }
}
