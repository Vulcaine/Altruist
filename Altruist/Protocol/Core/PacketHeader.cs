using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist;

[MessagePackObject]
public struct PacketHeader
{
    [JsonPropertyName("timestamp")]
    [Key(0)]
    public long Timestamp { get; set; }

    [JsonPropertyName("receiver")]
    [Key(1)]
    public string? Receiver { get; set; }

    [JsonPropertyName("sender")]
    [Key(2)]
    public string Sender { get; set; }

    public void Stamp(string sender, string receiver, DateTime tt)
        => (Sender, Receiver, Timestamp) = (sender, receiver, tt.Ticks);

    public void SetReceiver(string clientId) => Receiver = clientId;

    public void SetTimestamp(DateTime tt) => Timestamp = tt.Ticks;
}
