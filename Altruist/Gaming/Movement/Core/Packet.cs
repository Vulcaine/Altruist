using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist.Gaming.Movement;

[MessagePackObject]
public struct ForwardMovementPacket : IMovementPacket
{
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    [JsonPropertyName("moveUp")]
    [Key(1)]
    public bool MoveUp { get; set; }

    /// <summary>
    /// Indicates rotation direction:
    /// -1 = rotate left, 0 = no rotation, 1 = rotate right
    /// </summary>
    [JsonPropertyName("rotateLeftRight")]
    [Key(2)]
    public int RotateLeftRight { get; set; }

    [JsonPropertyName("turbo")]
    [Key(3)]
    public bool Turbo { get; set; }

    [JsonPropertyName("type")]
    [Key(4)]
    public string Type { get; set; } = "ForwardMovementPacket";

    public ForwardMovementPacket()
    {
        Header = default;
        MoveUp = false;
        Turbo = false;
        RotateLeftRight = 0;
        Type = "ForwardMovementPacket";
    }

    public ForwardMovementPacket(
        string sender,
        string? receiver = null,
        bool moveUp = false,
        bool turbo = false,
        int rotateLeftRight = 0)
    {
        Header = new PacketHeader(sender, receiver);
        MoveUp = moveUp;
        Turbo = turbo;
        RotateLeftRight = rotateLeftRight;
        Type = "ForwardMovementPacket";
    }
}

[MessagePackObject]
public struct EightDirectionMovementPacket : IMovementPacket
{
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>
    /// -1 = move down, 0 = no movement, 1 = move up
    /// </summary>
    [JsonPropertyName("moveUpDown")]
    [Key(1)]
    public int MoveUpDown { get; set; }

    /// <summary>
    /// -1 = move left, 0 = no movement, 1 = move right
    /// </summary>
    [JsonPropertyName("moveLeftRight")]
    [Key(2)]
    public int MoveLeftRight { get; set; }

    [JsonPropertyName("turbo")]
    [Key(3)]
    public bool Turbo { get; set; }

    [JsonPropertyName("type")]
    [Key(6)]
    public string Type { get; set; } = "EightDirectionMovementPacket";

    public EightDirectionMovementPacket()
    {
        Header = default;
        MoveUpDown = 0;
        MoveLeftRight = 0;
        Turbo = false;
    }

    public EightDirectionMovementPacket(
        string sender,
        string? receiver = null,
        int moveUpDown = 0,
        int moveLeftRight = 0,
        bool turbo = false)
    {
        Header = new PacketHeader(sender, receiver);
        MoveUpDown = moveUpDown;
        MoveLeftRight = moveLeftRight;
        Turbo = turbo;
        Type = "EightDirectionMovementPacket";
    }
}