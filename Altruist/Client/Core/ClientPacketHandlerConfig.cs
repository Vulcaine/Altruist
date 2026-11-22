using System.Reflection;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Client;

/// <summary>
/// Boot-time discovery: scans every loaded assembly for
/// <see cref="PacketHandlerAttribute"/>-marked classes, registers them as
/// singletons in DI and records them in <see cref="DiscoveredPacketHandlers"/>. The DI
/// <see cref="ClientPacketDispatcher"/> resolves and registers them from the application's provider on first use
/// (<see cref="ClientPacketDispatcher.RegisterDiscoveredHandlers"/>), so the handlers that receive packets are the
/// application's singletons. Mirrors the server's <c>CombatEventHandlerConfig</c>.
///
/// <para>Conditional on <c>altruist:client:transport</c> — without a client
/// transport configured, there are no inbound frames to dispatch, so we skip
/// discovery entirely.</para>
///
/// <para>Runs automatically as a <c>[ServiceConfiguration]</c> step (default order 0); you never call it. A handler
/// whose constructor dependencies cannot be resolved, or with an invalid <see cref="PacketAttribute"/> method, fails
/// <see cref="IAltruistClientRouter.ConnectAsync"/>.</para>
/// </summary>
[ServiceConfiguration]
[ConditionalOnConfig("altruist:client:transport")]
public sealed class ClientPacketHandlerConfig : IAltruistConfiguration
{
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Discovers <see cref="PacketHandlerAttribute"/> classes in all loaded assemblies, adds each as a singleton to
    /// <paramref name="services"/> and registers the list as <see cref="DiscoveredPacketHandlers"/>.
    /// </summary>
    /// <param name="services">Service collection being configured.</param>
    /// <returns>A completed task.</returns>
    public Task Configure(IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        Register(services, TypeDiscovery.FindTypesWithAttribute<PacketHandlerAttribute>(assemblies));
        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>Adds <paramref name="handlerTypes"/> as singletons and as the <see cref="DiscoveredPacketHandlers"/> list.</summary>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="handlerTypes">Handler classes (normally the <see cref="PacketHandlerAttribute"/> classes found by <see cref="Configure"/>).</param>
    public static void Register(IServiceCollection services, IEnumerable<Type> handlerTypes)
    {
        var types = handlerTypes.Distinct().ToArray();
        foreach (var handlerType in types)
            services.AddSingleton(handlerType);
        services.AddSingleton(new DiscoveredPacketHandlers(types));
    }
}

/// <summary>
/// The <see cref="PacketHandlerAttribute"/> classes found at boot by <see cref="ClientPacketHandlerConfig"/>; the DI
/// <see cref="ClientPacketDispatcher"/> registers them. Not for application code.
/// </summary>
public sealed class DiscoveredPacketHandlers
{
    /// <summary>Creates the list.</summary>
    /// <param name="types">Handler classes, each registered as a DI singleton.</param>
    public DiscoveredPacketHandlers(IReadOnlyList<Type> types) => Types = types;

    /// <summary>The handler classes.</summary>
    public IReadOnlyList<Type> Types { get; }
}
