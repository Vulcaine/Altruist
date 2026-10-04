namespace Altruist.Client;

/// <summary>
/// Method-level marker on a <see cref="PacketHandlerAttribute"/>-decorated class
/// declaring "this method handles packets of type <paramref name="packetType"/>".
/// Mirrors the server's <c>[CombatEvent(typeof(X))]</c> shape.
///
/// <para>Multiple methods MAY register for the same packet type — every matching
/// handler is invoked on dispatch (multi-sub). The wire <c>MessageCode</c> is
/// auto-detected by reading <see cref="IPacketBase.MessageCode"/> from a default
/// instance of <c>packetType</c>, so the packet itself remains the single source
/// of truth for routing.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PacketAttribute : Attribute
{
    public Type PacketType { get; }

    /// <summary>
    /// Optional explicit wire MessageCode. Set this when the packet type doesn't
    /// initialize <c>MessageCode</c> in its default constructor (for example,
    /// receive-only types where the server stamps the MC on the wire). When zero
    /// (the default), the dispatcher auto-detects the MC by reading
    /// <c>MessageCode</c> from a default instance.
    /// </summary>
    public uint MessageCode { get; init; }

    public PacketAttribute(Type packetType)
    {
        PacketType = packetType ?? throw new ArgumentNullException(nameof(packetType));
    }
}
