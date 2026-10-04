/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// DI-level service configuration. Scans assemblies for [Service] attributes
/// and registers them. Does NOT handle Portal/Gate discovery — that stays in Core.
/// </summary>
public class AltruistDIServiceConfig : IAltruistConfiguration
{
    private readonly ILogger _log;
    public bool IsConfigured { get; set; }

    public AltruistDIServiceConfig(ILogger log)
    {
        _log = log;
    }

    public Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();

        DependencyResolver.EnsureConverters(services, cfg, _log);

        var registered = new List<string>();
        RegisterBeanMethods(services, cfg, _log, registered);
        RegisterServiceAttributes(services, cfg, _log, registered);

        if (registered.Count > 0)
            _log.LogDebug("Registered services:\n{Services}", string.Join("\n", registered));

        return Task.CompletedTask;
    }

    private static IEnumerable<Type> Find<TAttr>() where TAttr : Attribute =>
        TypeDiscovery.FindTypesWithAttribute<TAttr>(
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)));

    public static void RegisterServiceAttributes(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg)
    {
        foreach (var implType in Find<ServiceAttribute>())
            RegisterServiceType(services, cfg, log, reg, implType);
    }

    public static void RegisterBeanMethods(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg)
    {
        foreach (var method in FindBeanMethods())
            RegisterBeanMethod(services, cfg, log, reg, method);
    }

    private static IEnumerable<MethodInfo> FindBeanMethods() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<BeanAttribute>(inherit: true) is not null);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static void RegisterBeanMethod(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg,
        MethodInfo method)
    {
        var attr = method.GetCustomAttribute<BeanAttribute>(inherit: true)!;

        if (method.ContainsGenericParameters)
        {
            log.LogWarning("Skipping bean method {Method} because open generic bean methods are not supported.", method.Name);
            return;
        }

        var returnType = method.ReturnType;
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            log.LogWarning("Skipping bean method {Method} because it does not return a service instance.", method.Name);
            return;
        }

        var serviceType = attr.ServiceType ?? returnType;
        if (!serviceType.IsAssignableFrom(returnType))
        {
            var msg =
                $"Bean method '{method.DeclaringType?.FullName}.{method.Name}' returns '{returnType.FullName}', " +
                $"which cannot be registered as '{serviceType.FullName}'.";
            DependencyResolver.FailAndExit(log, msg);
            throw new InvalidOperationException(msg);
        }

        if (attr.Replace)
            RemoveExistingBeanRegistrations(services, serviceType, attr.Name);

        object Factory(IServiceProvider sp)
        {
            var target = method.IsStatic
                ? null
                : DependencyResolver.CreateWithConfiguration(sp, cfg, method.DeclaringType!, log, attr.Lifetime);
            var args = method.GetParameters()
                .Select(p => DependencyResolver.ResolveParameter(sp, cfg, p, log))
                .ToArray();
            return method.Invoke(target, args)
                   ?? throw new InvalidOperationException($"Bean method '{method.Name}' returned null.");
        }

        if (attr.Name is null)
        {
            services.Add(new ServiceDescriptor(serviceType, Factory, attr.Lifetime));
            reg.Add($"\t{DependencyResolver.GetCleanName(serviceType)} <= bean {method.DeclaringType?.Name}.{method.Name} ({attr.Lifetime})");
            return;
        }

        switch (attr.Lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddKeyedSingleton(serviceType, attr.Name, (sp, _) => Factory(sp));
                break;
            case ServiceLifetime.Scoped:
                services.AddKeyedScoped(serviceType, attr.Name, (sp, _) => Factory(sp));
                break;
            default:
                services.AddKeyedTransient(serviceType, attr.Name, (sp, _) => Factory(sp));
                break;
        }

        reg.Add($"\t{DependencyResolver.GetCleanName(serviceType)}[{attr.Name}] <= bean {method.DeclaringType?.Name}.{method.Name} ({attr.Lifetime})");
    }

    private static void RemoveExistingBeanRegistrations(IServiceCollection services, Type serviceType, string? key)
    {
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != serviceType)
                continue;

            if (key is null)
            {
                if (!descriptor.IsKeyedService)
                    services.RemoveAt(i);
                continue;
            }

            if (descriptor.IsKeyedService && Equals(descriptor.ServiceKey, key))
                services.RemoveAt(i);
        }
    }

    public static void RegisterServiceType(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg,
        Type implType)
    {
        if (!DependencyResolver.ShouldRegister(implType, cfg, log))
            return;

        // A framework default steps aside for a registration the application made by hand.
        foreach (var missing in implType.GetCustomAttributes<ConditionalOnMissingServiceAttribute>(false))
        {
            if (services.Any(d => d.ServiceType == missing.ServiceType))
            {
                log.LogDebug("Skipping {Type}: {Service} is already registered.",
                    DependencyResolver.GetCleanName(implType), DependencyResolver.GetCleanName(missing.ServiceType));
                return;
            }
        }

        if (implType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0)
        {
            log.LogDebug("Skipping {Type} — no public constructors", implType.Name);
            return;
        }

        var conds = implType.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToArray();
        var listConds = conds.Where(c => !string.IsNullOrEmpty(c.KeyField)).ToArray();

        if (listConds.Length > 1)
        {
            log.LogError(
                "Type {Type} declares multiple ConditionalOnConfig attributes with KeyField. Only one list-style condition is supported.",
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

            if (listCond is null)
            {
                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, implType);

                // The planner may already have registered this type as a dependency of a service
                // processed earlier. A second descriptor would build a second singleton (and an
                // IEnumerable<T> would list it twice), so the planned registration is kept for the
                // implementation and an interface registration is turned into a forward to it.
                var selfIndex = DependencyResolver.IndexOfPlannedRegistration(services, implType, implType);
                if (selfIndex < 0)
                {
                    services.Add(new ServiceDescriptor(
                        implType,
                        sp => DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime)!,
                        lifetime));
                }
                else if (services[selfIndex].Lifetime != lifetime)
                {
                    services[selfIndex] = new ServiceDescriptor(
                        implType,
                        sp => DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime)!,
                        lifetime);
                }

                reg.Add($"\t{DependencyResolver.GetCleanName(implType)} → {DependencyResolver.GetCleanName(implType)} ({lifetime})");

                if (serviceType != implType)
                {
                    var forward = new ServiceDescriptor(
                        serviceType,
                        sp => sp.GetRequiredService(implType),
                        lifetime);
                    var plannedIndex = DependencyResolver.IndexOfPlannedRegistration(services, serviceType, implType);
                    if (plannedIndex < 0)
                        services.Add(forward);
                    else
                        services[plannedIndex] = forward;

                    reg.Add($"\t{DependencyResolver.GetCleanName(serviceType)} → {DependencyResolver.GetCleanName(implType)} ({lifetime})");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(listCond.KeyField))
            {
                var msg =
                    $"Type '{implType.FullName}' uses ConditionalOnConfig(Path='{listCond.Path}') for list-style registration, " +
                    "but KeyField is null or empty.";
                DependencyResolver.FailAndExit(log, msg);
                throw new InvalidOperationException(msg);
            }

            var listSection = cfg.GetSection(listCond.Path);

            if (!listSection.Exists())
            {
                log.LogDebug("ConditionalOnConfig list path '{Path}' not found — skipping {Type}",
                    listCond.Path, implType.Name);
                return;
            }

            var items = listSection.GetChildren().ToArray();
            if (items.Length == 0)
            {
                log.LogDebug("ConditionalOnConfig list path '{Path}' empty — skipping {Type}",
                    listCond.Path, implType.Name);
                return;
            }

            DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, implType);

            foreach (var itemSection in items)
            {
                var key = itemSection[listCond.KeyField!];
                if (string.IsNullOrWhiteSpace(key))
                {
                    var msg =
                        $"ConditionalOnConfig(Path='{listCond.Path}', KeyField='{listCond.KeyField}') for type '{implType.FullName}' expects each item to have a non-empty '{listCond.KeyField}'.";
                    DependencyResolver.FailAndExit(log, msg);
                    throw new InvalidOperationException(msg);
                }

                var itemKey = key;

                switch (lifetime)
                {
                    case ServiceLifetime.Singleton:
                        services.AddKeyedSingleton(
                            serviceType,
                            itemKey,
                            (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemSection, implType, log, lifetime));
                        break;

                    case ServiceLifetime.Scoped:
                        services.AddKeyedScoped(
                            serviceType,
                            itemKey,
                            (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemSection, implType, log, lifetime));
                        break;

                    default:
                        services.AddKeyedTransient(
                            serviceType,
                            itemKey,
                            (sp, _) => DependencyResolver.CreateWithConfiguration(sp, itemSection, implType, log, lifetime));
                        break;
                }

                services.Add(new ServiceDescriptor(
                    serviceType,
                    sp => sp.GetRequiredKeyedService(serviceType, itemKey),
                    lifetime));

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
}
