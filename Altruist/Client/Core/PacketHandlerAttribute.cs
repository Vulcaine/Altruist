namespace Altruist.Client;

/// <summary>
/// Class-level marker for a class that contains <see cref="PacketAttribute"/>-annotated
/// methods. Mirrors the server's <c>[CombatHandler]</c> opt-in convention — only
/// classes with this marker get scanned by <see cref="ClientPacketDispatcher"/>.
///
/// <para>Pair with <c>[Service]</c> so the DI container can construct the handler.</para>
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
