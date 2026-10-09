using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist;

/// <summary>
/// Routing metadata carried in <see cref="MessageEnvelope.Header"/>: who sent the
/// message, who it is for, and when it was stamped. MessagePack keys 0..2, JSON names
/// <c>timestamp</c>, <c>receiver</c>, <c>sender</c>.
/// </summary>
/// <remarks>
/// Mutable struct: the mutators only affect the instance they are called on. To change
/// an envelope's header use the forwarding methods on <see cref="MessageEnvelope"/>
/// (<see cref="MessageEnvelope.Stamp"/> etc.), which copy, mutate and write back.
/// For a server broadcast header use <see cref="PacketHeaders.Broadcast"/>.
/// </remarks>
[MessagePackObject]
public struct PacketHeader
{
    /// <summary>
    /// Stamp time as <see cref="DateTime.Ticks"/> (100 ns units since 0001-01-01); the server
    /// stamps with <see cref="DateTime.UtcNow"/>. 0 when never stamped.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [Key(0)]
    public long Timestamp { get; set; }

    /// <summary>Target client id; <c>null</c> for broadcasts (see <see cref="PacketHeaders.Broadcast"/>).</summary>
    [JsonPropertyName("receiver")]
    [Key(1)]
    public string? Receiver { get; set; }

    /// <summary>Sender id; <c>"server"</c> for server-originated messages.</summary>
    [JsonPropertyName("sender")]
    [Key(2)]
    public string Sender { get; set; }

    /// <summary>Sets <see cref="Sender"/>, <see cref="Receiver"/> and <see cref="Timestamp"/> in one call.</summary>
    /// <param name="sender">Sender id.</param>
    /// <param name="receiver">Receiver client id.</param>
    /// <param name="tt">Stamp time; stored as <see cref="DateTime.Ticks"/>.</param>
    public void Stamp(string sender, string receiver, DateTime tt)
        => (Sender, Receiver, Timestamp) = (sender, receiver, tt.Ticks);

    /// <summary>Sets <see cref="Receiver"/>.</summary>
    /// <param name="clientId">Receiver client id.</param>
    public void SetReceiver(string clientId) => Receiver = clientId;

    /// <summary>Sets <see cref="Timestamp"/> to <paramref name="tt"/>'s <see cref="DateTime.Ticks"/>.</summary>
    /// <param name="tt">Stamp time.</param>
    public void SetTimestamp(DateTime tt) => Timestamp = tt.Ticks;
}
