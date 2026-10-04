using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist;

[MessagePackObject]
public struct MessageEnvelope
{
    [Key(0)]
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; }

    [Key(1)]
    [JsonPropertyName("header")]
    public PacketHeader Header { get; set; }

    [Key(2)]
    [JsonPropertyName("message")]
    public object? Message { get; set; }

    public MessageEnvelope(PacketHeader header, IPacketBase message)
    {
        Header = header;
        Message = message;
        MessageCode = message.MessageCode;
    }

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
    public void Stamp(string sender, string receiver, DateTime receivedAt)
    {
        var header = Header;
        header.Stamp(sender, receiver, receivedAt);
        Header = header;
    }

    public void SetReceiver(string clientId)
    {
        var header = Header;
        header.SetReceiver(clientId);
        Header = header;
    }

    public void SetTimestamp(DateTime receivedAt)
    {
        var header = Header;
        header.SetTimestamp(receivedAt);
        Header = header;
    }
}
