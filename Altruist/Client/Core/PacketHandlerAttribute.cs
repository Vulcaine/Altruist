namespace Altruist.Client;

/// <summary>
/// Class-level marker for a class that contains <see cref="PacketAttribute"/>-annotated
/// methods. Mirrors the server's <c>[CombatHandler]</c> opt-in convention — only
/// classes with this marker are discovered at boot by <see cref="ClientPacketHandlerConfig"/>,
/// which registers them as DI singletons and passes the instance to
/// <see cref="ClientPacketDispatcher.Register"/>.
///
/// <para>The discovery step registers the class itself, so <c>[Service]</c> is optional
/// (add it only if you also want it exposed under an interface). Constructor dependencies
/// must be resolvable from DI, otherwise the class is skipped with a debug log. Discovery
/// only runs when <c>altruist:client:transport</c> is configured. Outside DI, skip this
/// attribute and call <see cref="ClientPacketDispatcher.Register"/> directly.</para>
///
/// <example>
/// <code>
/// [Service]
/// [PacketHandler]
/// public sealed class GameHandlers
/// {
///     [Packet(typeof(DamageInfo))]
///     public void OnDamage(DamageInfo dmg) { ... }
/// }
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PacketHandlerAttribute : Attribute
{
}
