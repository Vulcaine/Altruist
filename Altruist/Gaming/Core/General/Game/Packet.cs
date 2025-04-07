using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist.Gaming;

/// <summary>
/// Represents a general-purpose packet to notify clients of an object destruction event in the game world.
/// Typically used for world objects such as items, projectiles, or temporary entities.
/// </summary>
[MessagePackObject]
public struct DestroyObjectPacket : IPacketBase
{
    /// <summary>Packet routing header, determines the sender and optional receiver.</summary>
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Unique identifier of the object instance to be destroyed.</summary>
    [JsonPropertyName("instanceId")]
    [Key(1)]
    public string InstanceId { get; set; }

    /// <summary>Type identifier of the packet, used for routing and handling.</summary>
    [JsonPropertyName("type")]
    [Key(2)]
    public string Type { get; set; } = "DestroyObjectPacket";

    /// <summary>Default constructor for MessagePack deserialization.</summary>
    public DestroyObjectPacket()
    {
        InstanceId = string.Empty;
        Header = default;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DestroyObjectPacket"/> with the given sender and object instance ID.
    /// </summary>
    /// <param name="sender">The client ID or server origin sending the packet.</param>
    /// <param name="instanceId">The unique identifier of the object to destroy.</param>
    /// <param name="receiver">Optional receiver client ID for directed destruction notifications.</param>
    public DestroyObjectPacket(string sender, string instanceId, string? receiver = null)
    {
        InstanceId = instanceId;
        Header = new PacketHeader(sender, receiver);
    }
}

[MessagePackObject]
public class CreateObjectPacket : IPacketBase
{
    [Key(0)]
    [JsonPropertyName("header")]
    public PacketHeader Header { get; set; }

    [Key(1)]
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; }

    [Key(2)]
    [JsonPropertyName("objectType")]
    public string ObjectType { get; set; }

    [Key(3)]
    [JsonPropertyName("position")]
    public int[] Position { get; set; }

    [Key(4)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = "CreateObjectPacket";

    public CreateObjectPacket()
    {
        InstanceId = string.Empty;
        ObjectType = string.Empty;
        Position = Array.Empty<int>();
    }

    public CreateObjectPacket(string sender, string instanceId, string objectType, int[] position, string? receiver = null)
    {
        Header = new PacketHeader(sender, receiver);
        InstanceId = instanceId;
        ObjectType = objectType;
        Position = position;
    }
}

