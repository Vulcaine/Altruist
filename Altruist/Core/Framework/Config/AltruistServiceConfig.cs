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

        private static void RegisterServiceType(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg, Type implType)
        {
            if (!DependencyResolver.ShouldRegister(implType, cfg, log))
                return;

            foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
            {
                var lifetime = svcAttr.Lifetime;
                var serviceType = svcAttr.ServiceType ?? implType;

                // Register concrete implementation
                services.Add(new ServiceDescriptor(
                    implType,
                    sp =>
                    {
                        var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log);
                        try
                        { _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, log); }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on service {Type}.", implType.FullName);
                            throw;
                        }
                        return obj!;
                    },
                    lifetime));

                reg.Add($"\t{DependencyResolver.GetCleanName(implType)} → {DependencyResolver.GetCleanName(implType)} ({lifetime})");

                // Forward abstraction (if provided) to the same impl instance
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
            var provider = services.BuildServiceProvider();
            IAltruistContext settings = provider.GetRequiredService<IAltruistContext>();

            var portals = PortalDiscovery.Discover().Distinct();
            foreach (var d in portals)
            {
                var portalType = d.PortalType;
                if (!portalType.IsClass || portalType.IsAbstract)
                    continue;
                if (!DependencyResolver.ShouldRegister(portalType, cfg, log))
                    continue;

                services.Add(new ServiceDescriptor(
                    portalType,
                    sp =>
                    {
                        // Create instance + post-construct
                        var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, portalType, log);
                        try
                        { _ = DependencyResolver.InvokePostConstructAsync(instance, sp, cfg, log); }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "❌ PostConstruct failed on portal {Type}.", portalType.FullName);
                            throw;
                        }

                        // Register [Gate]-annotated methods from THIS instance
                        RegisterGateMethodsFromInstance(instance!, log);

                        return instance!;
                    },
                    ServiceLifetime.Transient));

                // Expose endpoint path in settings
                settings.AddEndpoint(d.Path);

                reg.Add($"\t{DependencyResolver.GetCleanName(portalType)} → {DependencyResolver.GetCleanName(portalType)} (Transient) [Portal]");
            }
        }

        // -------- gate registration helpers (moved from registry) --------

        private static void RegisterGateMethodsFromInstance(object instance, ILogger log)
        {
            var type = instance.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var gate = method.GetCustomAttribute<GateAttribute>();
                if (gate is null)
                    continue;

                ValidateGateMethodSignature(method);

                var parameters = method.GetParameters();
                var delegateType = Expression.GetDelegateType(
                    parameters.Select(p => p.ParameterType)
                              .Concat(new[] { method.ReturnType })
                              .ToArray());

                var del = method.CreateDelegate(delegateType, instance);

                // Register under the event name
                PortalGateRegistry<IPortal>.Register(gate.Event, del);

                log.LogDebug("🔐 Registered gate '{Event}' → {Type}.{Method}()", gate.Event, type.Name, method.Name);
            }
        }

        private static void ValidateGateMethodSignature(MethodInfo method)
        {
            if (method.ReturnType != typeof(Task))
                throw new InvalidOperationException($"[Gate] method {method.DeclaringType!.Name}.{method.Name} must return Task.");

            var p = method.GetParameters();

            if (p.Length == 1)
            {
                if (!typeof(IPacket).IsAssignableFrom(p[0].ParameterType))
                    throw new InvalidOperationException($"[Gate] {method.Name} must have signature (IPacket) or (IPacket, string).");
            }
            else if (p.Length == 2)
            {
                if (!typeof(IPacket).IsAssignableFrom(p[0].ParameterType) || p[1].ParameterType != typeof(string))
                    throw new InvalidOperationException($"[Gate] {method.Name} must have signature (IPacket) or (IPacket, string).");
            }
            else
            {
                throw new InvalidOperationException($"[Gate] {method.Name} must have exactly 1 or 2 parameters.");
            }
        }
    }
}
