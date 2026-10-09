using System.Reflection;
using Altruist.Contracts;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

/// <summary>
/// Runs once after DI bootstrap completes and resolves every <c>[CombatHandler]</c>
/// against the LIVE root container, then registers their <c>[CombatEvent]</c>
/// methods with <see cref="CombatEventHandlerRegistry"/>.
///
/// <para><b>Why this exists separately from <see cref="CombatEventHandlerConfig"/>:</b>
/// during <c>[ServiceConfiguration].Configure</c> the only way to instantiate
/// services is via a temporary <c>BuildServiceProvider()</c>. Singletons resolved
/// from that throwaway provider are NEW instances, isolated from the production
/// container's singletons — so a handler captured at warmup-time would carry a
/// disconnected <c>IConnectionStore</c> / <c>IAltruistRouter</c>, and every
/// <c>_router.Client.SendAsync(clientId, pkt)</c> would silently no-op because
/// the framework's <see cref="ClientSender"/> short-circuits when the socket
/// lookup returns null.</para>
///
/// <para>By moving the wiring to a <c>[PostConstruct]</c>, handler instances are
/// resolved from the same root container that the transport pipeline writes
/// connections to — so sends actually find a live socket.</para>
/// </summary>
public sealed class CombatHandlerInitializer
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<CombatHandlerInitializer> _logger;

    /// <summary>Created by DI; registered by <see cref="CombatEventHandlerConfig"/> only when handlers exist.</summary>
    /// <param name="provider">The live root service provider used to resolve handler instances.</param>
    /// <param name="logger">Logger for discovery output.</param>
    public CombatHandlerInitializer(IServiceProvider provider, ILogger<CombatHandlerInitializer> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// Invoked by the framework (<c>[PostConstruct]</c>) after the container is built; registers every handler
    /// method via <see cref="CombatEventHandlerDiscovery.RegisterCombatHandlers"/>. Do not call manually, or
    /// handlers are registered twice.
    /// </summary>
    [PostConstruct]
    public void Initialize()
    {
        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        CombatEventHandlerDiscovery.RegisterCombatHandlers(
            assemblies,
            type => _provider.GetService(type),
            _logger);

        _logger.LogDebug("Combat handler instances wired against live container.");
    }
}
