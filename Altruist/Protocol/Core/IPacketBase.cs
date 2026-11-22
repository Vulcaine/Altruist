using Altruist.Networking.Codec.MessagePack;
using MessagePack;

namespace Altruist;

/// <summary>
/// Contract every routable Altruist packet implements: a packet type identified on the
/// wire by its <see cref="MessageCode"/>. Shared by server and client so both sides
/// agree on the code-to-type mapping.
/// </summary>
/// <remarks>
/// <para>Give each packet type a unique, non-zero code as a property initializer so a
/// default-constructed instance already carries it — the client's
/// <c>Altruist.Client.ClientPacketDispatcher</c> reads the code from a default instance
/// to route inbound frames. Codes 1..999 are reserved for the framework
/// (<see cref="PacketCodes"/>); start your own at 1000+.</para>
/// <para>For MessagePack, decorate the concrete type with <c>[MessagePackObject]</c> and
/// <c>[Key(n)]</c> attributes. A field or property typed as the interface itself is
/// serialized through <see cref="PacketBaseFormatter"/> (polymorphic, reflection-based,
/// not AOT-safe); prefer concrete packet types on the wire.</para>
/// </remarks>
/// <example>
/// <code>
/// [MessagePackObject]
/// public sealed class ChatMessagePacket : IPacketBase
/// {
///     [Key(0)] public uint MessageCode { get; set; } = 1001;
///     [Key(1)] public string Text { get; set; } = "";
/// }
/// </code>
/// </example>
[MessagePackFormatter(typeof(PacketBaseFormatter))]
public interface IPacketBase : IPacket
{
    /// <summary>
    /// Wire routing code identifying the packet type. Copied into
    /// <see cref="MessageEnvelope.MessageCode"/> when the packet is enveloped. Must be
    /// non-zero (0 is reserved as "invalid"); built-in codes live in <see cref="PacketCodes"/>.
    /// </summary>
    uint MessageCode { get; set; }
}
