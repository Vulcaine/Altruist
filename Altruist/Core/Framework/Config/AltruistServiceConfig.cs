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

            DependencyResolver.EnsureConverters(services, cfg, logger);

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
        {
            if (reg.Count > 0)
                logger.LogDebug("✅ Registered services:\n{Services}", string.Join("\n", reg));
        }

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

            var conds = implType.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToArray();
            var listConds = conds.Where(c => !string.IsNullOrEmpty(c.KeyField)).ToArray();

            if (listConds.Length > 1)
            {
                log.LogError(
                    "Type {Type} declares multiple ConditionalOnConfig attributes with KeyField. " +
                    "Only one list-style condition is supported.",
                    implType.FullName);
                return;
            }

            var listCond = listConds.FirstOrDefault();

            foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
            {
                var lifetime = svcAttr.Lifetime;
                var serviceType = svcAttr.ServiceType ?? implType;

                if (svcAttr.DependsOn is { Length: > 0 })
                {
                    foreach (var depType in svcAttr.DependsOn)
                    {
                        if (depType is null)
                            continue;

                        if (!typeof(IAltruistConfiguration).IsAssignableFrom(depType))
                        {
                            log.LogWarning(
                                "Service {Service} declares DependsOn {Dep}, which does not implement IAltruistConfiguration. Ignoring.",
                                DependencyResolver.GetCleanName(implType),
                                depType.FullName);
                            continue;
                        }

                        ConfigAttributeConfiguration.EnsureConfigurationRegisteredAndConfigured(
                            services,
                            depType,
                            cfg,
                            log);
                    }
                }

                // ---------- Single-instance (no list condition) ----------
                if (listCond is null)
                {
                    DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, implType);

                    services.Add(new ServiceDescriptor(
                        implType,
                        sp =>
                        {
                            var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime);
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

                    continue;
                }

                // ---------- Multi-instance (list condition with KeyField) ----------

                if (string.IsNullOrWhiteSpace(listCond.KeyField))
                {
                    var msg =
                        $"❌ Type '{implType.FullName}' uses ConditionalOnConfig(Path='{listCond.Path}') " +
                        "for list-style registration, but KeyField is null or empty. " +
                        "When using a list condition you must specify KeyField.";
                    DependencyResolver.FailAndExit(log, msg);
                    throw new InvalidOperationException(msg);
                }

                var listSection = cfg.GetSection(listCond.Path);

                if (!listSection.Exists())
                {
                    var msg =
                        $"❌ ConditionalOnConfig list path '{listCond.Path}' for type '{implType.FullName}' " +
                        "does not exist in configuration.\n" +
                        $"   → Check your config file and make sure the path is correct.\n" +
                        $"   → Example: if your YAML has 'altruist:game:worlds:items', " +
                        $"      then the attribute should use \"altruist:game:worlds:items\", not \"altruist:worlds:items\".";
                    DependencyResolver.FailAndExit(log, msg);
                    throw new InvalidOperationException(msg);
                }

                var items = listSection.GetChildren().ToArray();
                if (items.Length == 0)
                {
                    var msg =
                        $"❌ ConditionalOnConfig list path '{listCond.Path}' for type '{implType.FullName}' " +
                        "exists but has no children. No instances will be registered.\n" +
                        "   → Add at least one item under this path in your configuration.";
                    DependencyResolver.FailAndExit(log, msg);
                    throw new InvalidOperationException(msg);
                }

                // Plan dependencies once using the *root* config
                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, implType);

                foreach (var itemSection in items)
                {
                    var itemCfg = itemSection;

                    var key = itemCfg[listCond.KeyField!];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        var msg =
                            $"❌ ConditionalOnConfig(Path='{listCond.Path}', KeyField='{listCond.KeyField}') " +
                            $"for type '{implType.FullName}' expects each item to have a non-empty '{listCond.KeyField}' field. " +
                            $"Item at path '{itemCfg.Path}' is missing or empty.";
                        DependencyResolver.FailAndExit(log, msg);
                        throw new InvalidOperationException(msg);
                    }

                    // Capture per-item key for closures
                    var itemKey = key;

                    // 1) Keyed registration for the service type (e.g. IWorldIndex3D["world1"])
                    switch (lifetime)
                    {
                        case ServiceLifetime.Singleton:
                            services.AddKeyedSingleton(
                                serviceType,
                                itemKey,
                                (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemCfg, implType, log, lifetime));
                            break;

                        case ServiceLifetime.Scoped:
                            services.AddKeyedScoped(
                                serviceType,
                                itemKey,
                                (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemCfg, implType, log, lifetime));
                            break;

                        case ServiceLifetime.Transient:
                        default:
                            services.AddKeyedTransient(
                                serviceType,
                                itemKey,
                                (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemCfg, implType, log, lifetime));
                            break;
                    }

                    // 2) Unkeyed alias for IWorldIndex3D so IEnumerable<IWorldIndex3D> returns ALL worlds.
                    services.Add(new ServiceDescriptor(
                        serviceType,
                        sp => sp.GetRequiredKeyedService(serviceType, itemKey),
                        lifetime));

                    // 3) Optionally, unkeyed alias for WorldIndex3D so IEnumerable<WorldIndex3D> also returns ALL worlds.
                    //    NOTE: injecting a single WorldIndex3D (GetRequiredService<WorldIndex3D>) will still just give *one*,
                    //    the last registration, so prefer IEnumerable or keyed resolution for this type.
                    if (serviceType != implType)
                    {
                        services.Add(new ServiceDescriptor(
                            implType,
                            sp => sp.GetRequiredKeyedService(serviceType, itemKey),
                            lifetime));
                    }

                    reg.Add($"\t{DependencyResolver.GetCleanName(serviceType)}[{itemKey}] → {DependencyResolver.GetCleanName(implType)} ({lifetime})");
                }
            }
        }

        private void RegisterPortals(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            // This IAltruistContext is only used to add endpoint metadata. Fine to use a temporary provider.
            var ctxProvider = services.BuildServiceProvider();
            var settings = ctxProvider.GetRequiredService<IAltruistContext>();

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
                        RegisterGateMethodsFromInstance(instance!, log);

                        if (instance is IPortal portalInstance)
                        {
                            var portalAttribute = portalInstance.GetType().GetCustomAttribute<PortalAttribute>();
                            if (portalAttribute is not null)
                            {
                                var basePath = cfg["altruist:server:transport:config:path"];
                                portalInstance.Route = PathUtils.NormalizeRoute(basePath, portalAttribute.Endpoint);
                            }

                            PortalGateRegistry<IPortal>.RegisterInstance(portalInstance);
                        }

                        return instance!;
                    },
                    ServiceLifetime.Transient));

                settings.AddEndpoint(d.Path);
                reg.Add($"\t{DependencyResolver.GetCleanName(portalType)} → {DependencyResolver.GetCleanName(portalType)} (Transient) [Portal]");
                toWarmUp.Add(portalType);
            }

            // Keep warm-up so [Gate] methods are registered eagerly,
            // but this still happens before the global PostConstruct pass.
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

        // ---------- Gate registration ----------

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
