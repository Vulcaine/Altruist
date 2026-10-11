using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming;

/// <summary>
/// Discovers all <see cref="IClientSessionCleanup"/> implementations at DI bootstrap
/// and registers each one as a singleton resolvable under
/// <see cref="IClientSessionCleanup"/> in addition to its concrete <c>[Service]</c>
/// registration. Lets <see cref="AltruistGameSessionPortal"/> inject
/// <c>IEnumerable&lt;IClientSessionCleanup&gt;</c> and fan out cleanup on disconnect
/// without users having to double-tag their classes with
/// <c>[Service(typeof(IClientSessionCleanup))]</c>.
///
/// <para>Mirrors the shape of <c>ClientPacketHandlerConfig</c> and
/// <c>CombatEventHandlerConfig</c> — runs during <c>Configure</c> phase, only
/// adds factory registrations (no warmup provider, no eager instantiation).
/// The factories resolve at runtime against the final container, so each
/// <see cref="IClientSessionCleanup"/> resolves to the same singleton instance
/// the rest of the app uses.</para>
/// </summary>
[ServiceConfiguration]
public sealed class ClientSessionCleanupConfiguration : IAltruistConfiguration
{
    /// <summary>Set once <see cref="Configure"/> ran.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Scans every loaded (non-dynamic) assembly for <see cref="IClientSessionCleanup"/> implementations and registers them (see <see cref="RegisterImplementations"/>).</summary>
    /// <param name="services">The service collection being built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public Task Configure(IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        var implTypes = TypeDiscovery
            .FindTypesImplementing<IClientSessionCleanup>(assemblies);

        RegisterImplementations(services, implTypes);

        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds an <see cref="IClientSessionCleanup"/> singleton factory for each
    /// concrete type, delegating to the live <see cref="IServiceProvider"/>.
    /// Exposed for tests that want to seed a controlled set of implementations
    /// without triggering an assembly-wide scan.
    /// </summary>
    public static void RegisterImplementations(IServiceCollection services, IEnumerable<Type> implTypes)
    {
        foreach (var t in implTypes)
        {
            // Capture the loop variable into a local — the lambda below is
            // evaluated lazily and a shared closure variable would resolve the
            // wrong type for every iteration except the last.
            var concrete = t;
            services.AddSingleton<IClientSessionCleanup>(
                sp => (IClientSessionCleanup)sp.GetRequiredService(concrete));
        }
    }
}
