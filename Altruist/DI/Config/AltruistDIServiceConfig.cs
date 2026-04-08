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

    public static void RegisterServiceType(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg,
        Type implType)
    {
        if (!DependencyResolver.ShouldRegister(implType, cfg, log))
            return;

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

                services.Add(new ServiceDescriptor(
                    implType,
                    sp => DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime)!,
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
