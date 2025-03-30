using System.Text.Json.Serialization;
using Altruist;
using MessagePack;

namespace Altruist
{
    // === Base Interfaces ===
    public interface IPacket : IModel
    {
        string Type { get; }
    }

    public interface IPacketBase : IPacket
    {
        PacketBase Header { get; }

        void SetReceiver(string clientId) => Header.SetReceiver(clientId);
    }

    public interface IMovementPacket : IPacketBase
    {

    }

    // === Common Header Struct ===

    [MessagePackObject]
    public struct PacketBase
    {
        [Key(0)] public long Timestamp { get; }

        [Key(1)] public string? Receiver { get; set; }
        [Key(2)] public string Sender { get; }

        public PacketBase()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Sender = "";
        }

        public void SetReceiver(string clientId) => Receiver = clientId;

        public PacketBase(string sender, string? receiver = null)
        {
            Sender = sender;
            Receiver = receiver;
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    // === Packet Structs ===
    [MessagePackObject]
    public struct SyncPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }

        [Key(1)] public string EntityType { get; set; }
        [Key(2)] public Dictionary<string, object?> Data { get; set; }

        public SyncPacket()
        {
            Header = default;
            EntityType = "";
            Data = new Dictionary<string, object?>();
        }

        public SyncPacket(string sender, string entityType, Dictionary<string, object?> data, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            EntityType = entityType;
            Data = data;
        }

        public string Type => "SyncPacket";


    }


    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }
        [JsonPropertyName("event")][Key(1)] public string Event { get; set; }

        public AltruistPacket()
        {
            Header = default;
            Event = "";
        }

        public AltruistPacket(string sender, string eventName, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            Event = eventName;
        }

        public string Type => "AltruistPacket";


    }


    [MessagePackObject]
    public struct SuccessPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public string Message { get; set; }

        public SuccessPacket()
        {
            Header = default;
            Message = "";
        }

        public SuccessPacket(string sender, string message, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            Message = message;
        }

        public string Type => "SuccessMessage";


    }


    [MessagePackObject]
    public struct JoinFailedPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public string Reason { get; set; }

        public JoinFailedPacket()
        {
            Header = default;
            Reason = "";
        }

        public JoinFailedPacket(string sender, string reason, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            Reason = reason;
        }

        public string Type => "JoinFailedPacket";


    }


    [MessagePackObject]
    public struct ShootingPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }

        public ShootingPacket()
        {
            Header = default;
        }

        public ShootingPacket(string sender, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
        }
        public string Type => "ShootingPacket";


    }


    [MessagePackObject]
    public struct ForwardMovementPacket : IMovementPacket
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public bool MoveUp { get; set; }
        [Key(2)] public bool RotateLeft { get; set; }
        [Key(3)] public bool RotateRight { get; set; }
        [Key(4)] public bool Turbo { get; set; }

        public ForwardMovementPacket() { }

        public ForwardMovementPacket(string sender, string? receiver = null, bool moveUp = false, bool rotateLeft = false, bool rotateRight = false, bool turbo = false)
        {
            Header = new PacketBase(sender, receiver);
            MoveUp = moveUp;
            Turbo = turbo;
            RotateLeft = rotateLeft;
            RotateRight = rotateRight;
        }
        public string Type => "ForwardMovementPacket";


    }



    [MessagePackObject]
    public struct EightDirectionMovementPacket : IMovementPacket
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public bool MoveUp { get; set; }
        [Key(2)] public bool MoveDown { get; set; }
        [Key(3)] public bool MoveLeft { get; set; }
        [Key(4)] public bool MoveRight { get; set; }
        [Key(5)] public bool Turbo { get; set; }

        public EightDirectionMovementPacket()
        {
            Header = default;
            MoveUp = false;
            MoveDown = false;
            MoveLeft = false;
            MoveRight = false;
            Turbo = false;
        }

        public EightDirectionMovementPacket(string sender, string? receiver = null, bool moveUp = false, bool moveDown = false, bool moveLeft = false, bool moveRight = false, bool turbo = false)
        {
            Header = new PacketBase(sender, receiver);
            MoveUp = moveUp;
            MoveDown = moveDown;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            Turbo = turbo;
        }
        public string Type => "EightDirectionMovementPacket";


    }

    // === Helper Classes ===

    [MessagePackObject]
    public struct Vector2Message
    {
        [Key(0)] public float X { get; set; }
        [Key(1)] public float Y { get; set; }

        public Vector2Message()
        {

        }

        public Vector2Message(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    [MessagePackObject]
    public struct JoinGamePacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public string Name { get; set; }
        [Key(2)] public string? RoomId { get; set; }
        [Key(2)] public float[]? Position { get; set; }

        public JoinGamePacket()
        {
            Header = default;
            Name = string.Empty;
            RoomId = string.Empty;
            Position = [0, 0];
        }

        public JoinGamePacket(string sender, string name, string? roomid = null, float[]? position = null, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            Name = name;
            RoomId = roomid;
            Position = position ?? [0, 0];
        }

        public string Type => "JoinGamePacket";
    }

    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }
        [Key(1)] public string ClientId { get; set; }

        public LeaveGamePacket()
        {
            Header = default;
            ClientId = string.Empty;
        }

        public LeaveGamePacket(string sender, string clientId, string? receiver = null)
        {
            Header = new PacketBase(sender, receiver);
            ClientId = clientId;
        }

        public string Type => "LeaveGamePacket";
    }

    [MessagePackObject]
    public struct RoomPacket : IPacketBase
    {
        [Key(0)] public PacketBase Header { get; set; }

        [Key(1)]
        public string Id { get; set; }

        [Key(2)]
        public uint MaxCapactiy { get; set; }

        [Key(3)]
        public HashSet<string> ConnectionIds { get; set; }

        public string Type => "RoomPacket";

        [IgnoreMember]
        public int PlayerCount => (ConnectionIds ?? new HashSet<string>()).Count;



        public RoomPacket()
        {
            Header = default;
            Id = string.Empty;
            MaxCapactiy = 100;
            ConnectionIds = new HashSet<string>();
        }

        public RoomPacket(string roomId, uint maxCapacity = 100)
        {
            Id = roomId;
            MaxCapactiy = maxCapacity;
            ConnectionIds = new HashSet<string>();
        }

        public bool Has(string connectionId) => ConnectionIds.Contains(connectionId);

        public bool Full()
        {
            return PlayerCount >= MaxCapactiy;
        }

        public bool Empty()
        {
            return PlayerCount == 0;
        }

        public bool IsDefault()
        {
            return EqualityComparer<RoomPacket>.Default.Equals(this, default);
        }

        public RoomPacket AddConnection(string connectionId)
        {
            var newRoomPacket = this;
            newRoomPacket.ConnectionIds.Add(connectionId);
            return newRoomPacket;
        }

        public RoomPacket RemoveConnection(string connectionId)
        {
            var newRoomPacket = this;
            newRoomPacket.ConnectionIds.Remove(connectionId);
            return newRoomPacket;
        }

        public override string ToString()
        {
            return $"Room[{Id}]: {PlayerCount}/{MaxCapactiy}";
        }
    }

}

public class PacketHelper
{
    public static JoinFailedPacket JoinFailed(string reason, string receiver) => new JoinFailedPacket("server", reason, receiver);

    public static SuccessPacket Success(string message, string receiver)
    {
        return new SuccessPacket("server", message, receiver);
    }
}