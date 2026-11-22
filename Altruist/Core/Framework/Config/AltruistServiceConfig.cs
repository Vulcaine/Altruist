/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.Contracts;
using Altruist.Web.Features;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// Full framework service configuration. Extends DI-level service registration
/// with Portal/Gate discovery for the server runtime.
/// </summary>
/// <remarks>
/// Run by <see cref="AltruistBootstrap.BootstrapServices"/>. Registers beans and <see cref="ServiceAttribute"/> classes
/// (same rules as <see cref="AltruistDIServiceConfig"/>), then every discovered portal as a transient service under its own
/// type (honouring <see cref="ConditionalOnConfigAttribute"/>), with its route set from its <c>[Portal]</c> endpoint.
/// </remarks>
public class AltruistServiceConfig : IAltruistConfiguration
{
    private readonly ILogger _log;
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>Creates the configuration step.</summary>
    /// <param name="log">Logger for registration diagnostics.</param>
    public AltruistServiceConfig(ILogger log)
    {
        _log = log;
    }

    /// <summary>Registers beans, services and portals into <paramref name="services"/>.</summary>
    /// <param name="services">Collection to register into.</param>
    public Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();

        DependencyResolver.EnsureConverters(services, cfg, _log);

        var registered = new List<string>();

        // Reuse DI-level bean/service registration.
        AltruistDIServiceConfig.RegisterBeanMethods(services, cfg, _log, registered);
        AltruistDIServiceConfig.RegisterServiceAttributes(services, cfg, _log, registered);

        // Full framework addition: Portal discovery
        RegisterPortals(services, cfg, _log, registered);

        if (registered.Count > 0)
            _log.LogDebug("Registered services:\n{Services}", string.Join("\n", registered));

        return Task.CompletedTask;
    }

    private static void RegisterPortals(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        List<string> reg)
    {
        var portals = PortalDiscovery.Discover().Distinct().ToArray();

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
                    var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, portalType, log)!;

                    if (instance is IPortal portalInstance)
                    {
                        var portalAttribute = portalInstance.GetType().GetCustomAttribute<PortalAttribute>();
                        if (portalAttribute is not null)
                        {
                            portalInstance.Route = PathUtils.NormalizeRoute(portalAttribute.Endpoint);
                        }
                    }

                    return instance!;
                },
                ServiceLifetime.Transient));

            reg.Add($"\t{DependencyResolver.GetCleanName(portalType)} → {DependencyResolver.GetCleanName(portalType)} (Transient) [Portal]");
        }
    }
}
