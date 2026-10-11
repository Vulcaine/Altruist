using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist;

/// <summary>
/// Outer frame of every server-to-client message: a MessagePack fixarray(3)
/// <c>[MessageCode, Header, Message]</c> (or the JSON object
/// <c>{ "messageCode", "header", "message" }</c>). The code is duplicated at the front so
/// receivers can route a frame without deserializing the inner packet.
/// </summary>
/// <remarks>
/// <para>The server builds these when sending (see its router / outbound queue, which call
/// <see cref="MessageEnvelope(IPacketBase, string)"/> followed by <see cref="Stamp"/>).
/// Clients normally never construct one; <c>Altruist.Client.MessageEnvelopeShape</c> peeks
/// the code and slices out <see cref="Message"/> at the byte level so the inner packet can
/// be deserialized as its concrete type (AOT-safe).</para>
/// <para>This is a mutable struct: call the mutators on a variable (not on a copy returned
/// from a property or a readonly field), otherwise the change is lost.</para>
/// </remarks>
[MessagePackObject]
public struct MessageEnvelope
{
    /// <summary>
    /// Routing code of the inner packet (copy of <see cref="IPacketBase.MessageCode"/>).
    /// Key 0 on the MessagePack wire.
    /// </summary>
    [Key(0)]
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; }

    /// <summary>Sender / receiver / timestamp metadata. Key 1 on the MessagePack wire.</summary>
    [Key(1)]
    [JsonPropertyName("header")]
    public PacketHeader Header { get; set; }

    /// <summary>
    /// The inner packet (normally an <see cref="IPacketBase"/>). Typed as <see cref="object"/>
    /// so it is serialized with the runtime type's own formatter. Key 2 on the MessagePack wire.
    /// </summary>
    [Key(2)]
    [JsonPropertyName("message")]
    public object? Message { get; set; }

    /// <summary>
    /// Wraps <paramref name="message"/> with an explicit header (e.g.
    /// <see cref="PacketHeaders.Broadcast"/>). <see cref="MessageCode"/> is copied from the packet.
    /// </summary>
    /// <param name="header">Header to attach as-is.</param>
    /// <param name="message">Packet to wrap; must not be <c>null</c>.</param>
    public MessageEnvelope(PacketHeader header, IPacketBase message)
    {
        Header = header;
        Message = message;
        MessageCode = message.MessageCode;
    }

    /// <summary>
    /// Wraps a server-originated <paramref name="message"/> addressed to one client:
    /// header sender is <c>"server"</c>, receiver is <paramref name="receiver"/>, timestamp
    /// is left at 0 (call <see cref="Stamp"/> or <see cref="SetTimestamp"/> to set it).
    /// </summary>
    /// <param name="message">Packet to wrap; must not be <c>null</c>.</param>
    /// <param name="receiver">Target client id.</param>
    public MessageEnvelope(IPacketBase message, string receiver)
    {
        Header = new PacketHeader
        {
            Sender = "server",
            Receiver = receiver
        };
        Message = message;
        MessageCode = message.MessageCode;
    }

    // Header is a struct exposed through a property, so mutating `Header.X(...)` directly would
    // only change a temporary copy. Copy, mutate, write back.
    /// <summary>
    /// Sets sender, receiver and timestamp on <see cref="Header"/> in one step
    /// (see <see cref="PacketHeader.Stamp"/>). Use this instead of mutating
    /// <c>Header</c> directly, which would only change a copy.
    /// </summary>
    /// <param name="sender">Sender id (the server uses <c>"server"</c>).</param>
    /// <param name="receiver">Receiver client id.</param>
    /// <param name="receivedAt">Time stored as <see cref="DateTime.Ticks"/> (the server passes <see cref="DateTime.UtcNow"/>).</param>
    public void Stamp(string sender, string receiver, DateTime receivedAt)
    {
        var header = Header;
        header.Stamp(sender, receiver, receivedAt);
        Header = header;
    }

    /// <summary>Sets <see cref="PacketHeader.Receiver"/> on <see cref="Header"/>.</summary>
    /// <param name="clientId">Receiver client id.</param>
    public void SetReceiver(string clientId)
    {
        var header = Header;
        header.SetReceiver(clientId);
        Header = header;
    }

    /// <summary>Sets <see cref="PacketHeader.Timestamp"/> on <see cref="Header"/> to <paramref name="receivedAt"/>'s ticks.</summary>
    /// <param name="receivedAt">Time stored as <see cref="DateTime.Ticks"/>.</param>
    public void SetTimestamp(DateTime receivedAt)
    {
        var header = Header;
        header.SetTimestamp(receivedAt);
        Header = header;
    }
}
