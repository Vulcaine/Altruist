/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;
using System.Reflection;

using Altruist.Contracts;
using Altruist.Web.Features;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public class AltruistServiceConfig : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        public Task Configure(IServiceCollection services)
        {
            var cfg = GetConfig();
            var logger = GetLogger(services);

            DependencyResolver.EnsureConverters(services, logger);

            var registered = new List<string>();
            RegisterServiceAttributes(services, cfg, logger, registered);
            RegisterPortals(services, cfg, logger, registered);

            LogRegistered(logger, registered);
            return Task.CompletedTask;
        }

        // ---------- High-level helpers ----------

        private static IConfiguration GetConfig() => AppConfigLoader.Load();

        private static ILogger GetLogger(IServiceCollection services) =>
            services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger<AltruistServiceConfig>();

        private static Assembly[] GetAssemblies() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)).ToArray();

        private static IEnumerable<Type> Find<TAttr>() where TAttr : Attribute =>
            TypeDiscovery.FindTypesWithAttribute<TAttr>(GetAssemblies());

        private static void LogRegistered(ILogger logger, List<string> reg)
        { if (reg.Count > 0) logger.LogDebug("✅ Registered services:\n{Services}", string.Join("\n", reg)); }

        // ---------- Service & Portal registration ----------

        private static void RegisterServiceAttributes(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            foreach (var implType in Find<ServiceAttribute>())
                RegisterServiceType(services, cfg, log, reg, implType);
        }

        private static void RegisterServiceType(
    IServiceCollection services,
    IConfiguration cfg,
    ILogger log,
    List<string> reg,
    Type implType)
        {
            if (!DependencyResolver.ShouldRegister(implType, cfg, log))
                return;

            foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
            {
                var lifetime = svcAttr.Lifetime;
                var serviceType = svcAttr.ServiceType ?? implType;

                if (svcAttr.DependsOn is { Length: > 0 })
                {
                    using var tmpProvider = services.BuildServiceProvider();
                    var depLogger = tmpProvider.GetRequiredService<ILoggerFactory>().CreateLogger<AltruistServiceConfig>();

                    foreach (var depType in svcAttr.DependsOn)
                    {
                        if (depType is null)
                            continue;

                        if (!typeof(IAltruistConfiguration).IsAssignableFrom(depType))
                        {
                            depLogger.LogWarning(
                                "Service {Service} declares DependsOn {Dep}, which does not implement IAltruistConfiguration. Ignoring.",
                                DependencyResolver.GetCleanName(implType),
                                depType.FullName);
                            continue;
                        }

                        var depInstance = tmpProvider.GetService(depType) as IAltruistConfiguration;
                        if (depInstance is null)
                        {
                            var msg =
                                $"Service {DependencyResolver.GetCleanName(implType)} depends on configuration {depType.FullName}, " +
                                "but it is not registered. Ensure the configuration class is annotated with [ServiceConfiguration] " +
                                "and discovered by ConfigAttributeConfiguration.";
                            depLogger.LogCritical(msg);
                            throw new InvalidOperationException(msg);
                        }

                        if (!depInstance.IsConfigured)
                        {
                            var msg =
                                $"Service {DependencyResolver.GetCleanName(implType)} depends on configuration {depType.FullName}, " +
                                "but it has not finished Configure() yet (IsConfigured == false). " +
                                "Check your [ServiceConfiguration(Order=...)] setup so that {depType.Name} runs before service registration.";
                            depLogger.LogCritical(msg);
                            throw new InvalidOperationException(msg);
                        }
                    }
                }

                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, implType);

                services.Add(new ServiceDescriptor(
                    implType,
                    sp =>
                    {
                        var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime);
                        try
                        {
                            _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, log);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on service {Type}.", implType.FullName);
                            throw;
                        }
                        return obj!;
                    },
                    lifetime));

                reg.Add($"\t{DependencyResolver.GetCleanName(implType)} → {DependencyResolver.GetCleanName(implType)} ({lifetime})");

                if (serviceType != implType)
                {
                    services.Add(new ServiceDescriptor(
                        serviceType,
                        sp => sp.GetRequiredService(implType),
                        lifetime));

                    reg.Add($"\t{DependencyResolver.GetCleanName(serviceType)} → {DependencyResolver.GetCleanName(implType)} ({lifetime})");
                }
            }
        }

        private void RegisterPortals(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            var settings = services.BuildServiceProvider().GetRequiredService<IAltruistContext>();

            var portals = PortalDiscovery.Discover().Distinct().ToArray();
            var toWarmUp = new List<Type>();

            foreach (var d in portals)
            {
                var portalType = d.PortalType;
                if (!portalType.IsClass || portalType.IsAbstract)
                    continue;
                if (!DependencyResolver.ShouldRegister(portalType, cfg, log))
                    continue;

                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, portalType);

                services.Add(new ServiceDescriptor(
                    portalType,
                    sp =>
                    {
                        var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, portalType, log);
                        try
                        {
                            _ = DependencyResolver.InvokePostConstructAsync(instance, sp, cfg, log);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on portal {Type}.", portalType.FullName);
                            throw;
                        }

                        RegisterGateMethodsFromInstance(instance!, log);

                        if (instance is IPortal portalInstance)
                            PortalGateRegistry<IPortal>.RegisterInstance(portalInstance);

                        return instance!;
                    },
                    ServiceLifetime.Transient));

                settings.AddEndpoint(d.Path);
                reg.Add($"\t{DependencyResolver.GetCleanName(portalType)} → {DependencyResolver.GetCleanName(portalType)} (Transient) [Portal]");
                toWarmUp.Add(portalType);
            }

            if (toWarmUp.Count > 0)
            {
                using var warmupProvider = services.BuildServiceProvider();
                foreach (var t in toWarmUp)
                {
                    try
                    {
                        _ = warmupProvider.GetRequiredService(t);
                        log.LogDebug("🔥 Warmed up portal {PortalType} to register [Gate] methods.", t.FullName);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "❌ Failed to warm up portal {PortalType}.", t.FullName);
                        throw;
                    }
                }
            }
        }

        private static void RegisterGateMethodsFromInstance(object instance, ILogger log)
        {
            var type = instance.GetType();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .Where(m => m.GetCustomAttribute<GateAttribute>() != null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<GateAttribute>()!;
                var pars = method.GetParameters();

                var rt = method.ReturnType;
                var isTask = rt == typeof(Task) ||
                             rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>);

                if (!isTask)
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [Gate] must return Task or Task<T>.");

                // --- Parameters:
                //  0 params           -> ok
                //  1 param            -> must be string clientId
                //  2 params           -> first IPacket, second string clientId
                //  >2 params          -> invalid
                if (pars.Length > 2)
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} marked with [Gate] must have 0, 1 or 2 parameters.");

                if (pars.Length == 1)
                {
                    if (pars[0].ParameterType != typeof(string))
                        throw new InvalidOperationException(
                            $"Method {type.Name}.{method.Name} with a single parameter must have signature (string clientId).");
                }
                else if (pars.Length == 2)
                {
                    if (!typeof(IPacket).IsAssignableFrom(pars[0].ParameterType))
                        throw new InvalidOperationException(
                            $"Method {type.Name}.{method.Name} first parameter must implement IPacket.");
                    if (pars[1].ParameterType != typeof(string))
                        throw new InvalidOperationException(
                            $"Method {type.Name}.{method.Name} second parameter must be string (clientId).");
                }

                var delegateType = Expression.GetDelegateType(
                    pars.Select(p => p.ParameterType)
                        .Concat(new[] { typeof(Task) })
                        .ToArray());

                var del = method.CreateDelegate(delegateType, instance);
                PortalGateRegistry<IPortal>.Register(attr.Event, del);

                log.LogDebug("🔒 Registered gate for event '{Event}' -> {Method} on {Type}.",
                    attr.Event, method.Name, type.FullName);
            }
        }
    }
}
