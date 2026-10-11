using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Client.Inventory;

/// <summary>
/// Forwards the <see cref="IClientInventoryService"/> abstraction to the
/// concrete singleton that <see cref="ClientPacketHandlerConfig"/> already
/// registered (because <see cref="ClientInventoryService"/> carries
/// <see cref="PacketHandlerAttribute"/>).
///
/// <para>Without this forwarder, two registrations would coexist — one
/// implicit one against the concrete type, and a hypothetical second one
/// against the interface — and the dispatcher would call <see cref="ClientInventoryService.OnItemSnapshot"/>
/// on a different instance than the one consumers query through
/// <see cref="IClientInventoryService"/>. We deliberately resolve the
/// concrete singleton inside the factory so both DI keys point at the same
/// object.</para>
///
/// <para>Order is set high enough to run after <see cref="ClientPacketHandlerConfig"/>
/// (default <c>0</c>) so the concrete singleton is registered before we
/// forward.</para>
///
/// <para>Runs automatically as a <c>[ServiceConfiguration]</c> step; you never call it.
/// Like <see cref="ClientPacketHandlerConfig"/> it only runs when <c>altruist:client:transport</c> is configured (the
/// concrete service exists only then), so <see cref="IClientInventoryService"/> is not registered without a client
/// transport.</para>
/// </summary>
[ServiceConfiguration(order: 100)]
[ConditionalOnConfig("altruist:client:transport")]
public sealed class ClientInventoryServiceConfig : IAltruistConfiguration
{
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>Adds a singleton <see cref="IClientInventoryService"/> registration that resolves
    /// the concrete <see cref="ClientInventoryService"/> singleton.</summary>
    /// <param name="services">Service collection being configured.</param>
    /// <returns>A completed task.</returns>
    public Task Configure(IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.AddSingleton<IClientInventoryService>(sp =>
            sp.GetRequiredService<ClientInventoryService>());

        IsConfigured = true;
        return Task.CompletedTask;
    }
}
