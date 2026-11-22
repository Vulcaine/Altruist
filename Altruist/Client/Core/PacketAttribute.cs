namespace Altruist.Client;

/// <summary>
/// Method-level marker on a <see cref="PacketHandlerAttribute"/>-decorated class
/// declaring "this method handles packets of type <c>packetType</c>".
/// Mirrors the server's <c>[CombatEvent(typeof(X))]</c> shape.
///
/// <para>Multiple methods MAY register for the same packet type — every matching
/// handler is invoked on dispatch (multi-sub). The wire <c>MessageCode</c> is
/// auto-detected by reading <see cref="IPacketBase.MessageCode"/> from a default
/// instance of <c>packetType</c>, so the packet itself remains the single source
/// of truth for routing.</para>
///
/// <para>The method must be a public or non-public <b>instance</b> method returning
/// <c>void</c> with exactly one parameter whose type equals <see cref="PacketType"/>;
/// violations throw from <see cref="ClientPacketDispatcher.Register"/>. Handlers run on the
/// dispatching thread (see <see cref="ClientPacketDispatcher"/>).</para>
/// </summary>
/// <example>
/// <code>
/// [PacketHandler]
/// public sealed class ChatHandlers
/// {
///     [Packet(typeof(ChatMessagePacket))]                    // MessageCode read from new ChatMessagePacket()
///     public void OnChat(ChatMessagePacket msg) { ... }
///
///     [Packet(typeof(ServerNotice), MessageCode = 1500)]     // receive-only type without a default code
///     public void OnNotice(ServerNotice notice) { ... }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PacketAttribute : Attribute
{
    /// <summary>Concrete packet type the method receives; also the type the inner payload is deserialized as.</summary>
    public Type PacketType { get; }

    /// <summary>
    /// Optional explicit wire MessageCode. Set this when the packet type doesn't
    /// initialize <c>MessageCode</c> in its default constructor (for example,
    /// receive-only types where the server stamps the MC on the wire). When zero
    /// (the default), the dispatcher auto-detects the MC by reading
    /// <c>MessageCode</c> from a default instance.
    /// </summary>
    public uint MessageCode { get; init; }

    /// <summary>Declares the method as a handler for <paramref name="packetType"/>.</summary>
    /// <param name="packetType">Concrete packet type; must match the method's single parameter type exactly.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packetType"/> is <c>null</c>.</exception>
    public PacketAttribute(Type packetType)
    {
        PacketType = packetType ?? throw new ArgumentNullException(nameof(packetType));
    }
}
