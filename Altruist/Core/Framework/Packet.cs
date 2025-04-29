/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json.Serialization;
using Altruist;
using Altruist.Networking.Codec.MessagePack;
using MessagePack;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace Altruist
{

    public static class PacketHeaders
    {
        public static PacketHeader Broadcast = new PacketHeader("server");
    }

    // === Base Interfaces ===
    public interface IPacket : ITypedModel
    {
    }

    [MessagePackFormatter(typeof(PacketBaseFormatter))]
    public interface IPacketBase : IPacket
    {
        PacketHeader Header { get; set; }

        void SetReceiver(string clientId) => Header.SetReceiver(clientId);
    }

    [MessagePackObject]
    public struct DecodeType : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        [JsonPropertyName("type")][Key(1)] public string Type { get; set; }
        public DecodeType()
        {
            Header = default;
            Type = "";
        }

        public DecodeType(PacketHeader header, string type)
        {
            Header = header;
            Type = type;
        }
    }

    [MessagePackObject]
    public struct InterprocessPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("processId")][Key(1)] public string ProcessId { get; set; }
        [JsonPropertyName("message")][Key(2)] public IPacketBase Message { get; set; }
        [JsonPropertyName("type")][Key(3)] public string Type { get; set; } = "InterprocessPacket";

        public InterprocessPacket(string processId, IPacketBase message)
        {
            Header = message.Header;
            Message = message;
            ProcessId = processId;
        }

        public InterprocessPacket()
        {
            Header = default;
            Message = default!;
            ProcessId = "";
        }
    }

    public interface IMovementPacket : IPacketBase
    {

    }

    // === Common Header Struct ===

    [MessagePackObject]
    public struct PacketHeader
    {
        [JsonPropertyName("header")][Key(0)] public long Timestamp { get; }

        [JsonPropertyName("receiver")][Key(1)] public string? Receiver { get; set; }
        [JsonPropertyName("sender")][Key(2)] public string Sender { get; }

        public PacketHeader()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Sender = "";
        }

        public void SetReceiver(string clientId) => Receiver = clientId;

        public PacketHeader(string sender, string? receiver = null)
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
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        [JsonPropertyName("entityType")][Key(1)] public string EntityType { get; set; }
        [JsonPropertyName("data")][Key(2)] public Dictionary<string, object?> Data { get; set; }

        public SyncPacket()
        {
            Header = default;
            EntityType = "";
            Data = new Dictionary<string, object?>();
        }

        public SyncPacket(string sender, string entityType, Dictionary<string, object?> data, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            EntityType = entityType;
            Data = data;
        }

        [JsonPropertyName("type")][Key(3)] public string Type { get; set; } = "SyncPacket";
    }


    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("event")][Key(1)] public string Event { get; set; }

        public AltruistPacket()
        {
            Header = default;
            Event = "";
        }

        public AltruistPacket(string sender, string eventName, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            Event = eventName;
        }

        [JsonPropertyName("type")][Key(2)] public string Type { get; set; } = "AltruistPacket";


    }


    [MessagePackObject]
    public struct SuccessPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("message")][Key(1)] public string Message { get; set; }
        [JsonPropertyName("successType")][Key(2)] public string SuccessType { get; set; }

        [JsonPropertyName("type")][Key(3)] public string Type { get; set; } = "SuccessMessage";

        public SuccessPacket()
        {
            Header = default;
            Message = "";
            SuccessType = "";
        }

        public SuccessPacket(string sender, string message, string successType, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            Message = message;
            SuccessType = successType;
        }
    }

    [MessagePackObject]
    public struct FailedPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("reason")][Key(1)] public string Reason { get; set; }

        [JsonPropertyName("message")][Key(2)] public string FailType { get; set; }

        [JsonPropertyName("type")][Key(3)] public string Type { get; set; } = "FailedPacket";

        public FailedPacket()
        {
            Header = default;
            Reason = "";
            FailType = "";
        }

        public FailedPacket(string sender, string reason, string failType, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            Reason = reason;
            FailType = failType;
        }


    }


    [MessagePackObject]
    public struct ShootingPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        public ShootingPacket()
        {
            Header = default;
        }

        public ShootingPacket(string sender, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
        }
        [JsonPropertyName("type")][Key(1)] public string Type { get; set; } = "ShootingPacket";


    }


    [MessagePackObject]
    public struct ForwardMovementPacket : IMovementPacket
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("moveUp")][Key(1)] public bool MoveUp { get; set; }
        [JsonPropertyName("rotateLeft")][Key(2)] public bool RotateLeft { get; set; }
        [JsonPropertyName("rotateRight")][Key(3)] public bool RotateRight { get; set; }
        [JsonPropertyName("turbo")][Key(4)] public bool Turbo { get; set; }

        [JsonPropertyName("type")][Key(5)] public string Type { get; set; } = "ForwardMovementPacket";

        public ForwardMovementPacket() { }

        public ForwardMovementPacket(string sender, string? receiver = null, bool moveUp = false, bool rotateLeft = false, bool rotateRight = false, bool turbo = false)
        {
            Header = new PacketHeader(sender, receiver);
            MoveUp = moveUp;
            Turbo = turbo;
            RotateLeft = rotateLeft;
            RotateRight = rotateRight;
        }
    }

    [MessagePackObject]
    public struct EightDirectionMovementPacket : IMovementPacket
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("moveUp")][Key(1)] public bool MoveUp { get; set; }
        [JsonPropertyName("moveDown")][Key(2)] public bool MoveDown { get; set; }
        [JsonPropertyName("moveLeft")][Key(3)] public bool MoveLeft { get; set; }
        [JsonPropertyName("moveRight")][Key(4)] public bool MoveRight { get; set; }
        [JsonPropertyName("turbo")][Key(5)] public bool Turbo { get; set; }

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
            Header = new PacketHeader(sender, receiver);
            MoveUp = moveUp;
            MoveDown = moveDown;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            Turbo = turbo;
        }
        [JsonPropertyName("type")][Key(6)] public string Type { get; set; } = "EightDirectionMovementPacket";


    }

    // === Helper Classes ===

    [MessagePackObject]
    public struct Vector2Message
    {
        [JsonPropertyName("x")][Key(0)] public float X { get; set; }
        [JsonPropertyName("y")][Key(1)] public float Y { get; set; }

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
    public struct HandshakePacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("rooms")][Key(1)] public RoomPacket[] Rooms { get; set; }

        [JsonPropertyName("type")][Key(2)] public string Type { get; set; } = "HandshakePacket";

        public HandshakePacket()
        {
            Header = default;
            Rooms = Array.Empty<RoomPacket>();
        }

        public HandshakePacket(string sender, RoomPacket[] rooms, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            Rooms = rooms;
        }

    }

    [MessagePackObject]
    public struct JoinGamePacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("name")][Key(1)] public string Name { get; set; }
        [JsonPropertyName("roomId")][Key(2)] public string? RoomId { get; set; }
        [JsonPropertyName("world")][Key(3)] public int? WorldIndex { get; set; }
        [JsonPropertyName("position")][Key(4)] public float[]? Position { get; set; }
        [JsonPropertyName("type")][Key(5)] public string Type { get; set; } = "JoinGamePacket";

        public JoinGamePacket()
        {
            Header = default;
            Name = string.Empty;
            RoomId = string.Empty;
            Position = [0, 0];
            WorldIndex = 0;
        }

        public JoinGamePacket(string sender, string name, string? roomid = null, int? worldIndex = 0, float[]? position = null, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            Name = name;
            RoomId = roomid;
            Position = position ?? [0, 0];
            WorldIndex = worldIndex;
        }


    }

    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("clientId")][Key(1)] public string ClientId { get; set; }

        public LeaveGamePacket()
        {
            Header = default;
            ClientId = string.Empty;
        }

        public LeaveGamePacket(string sender, string clientId, string? receiver = null)
        {
            Header = new PacketHeader(sender, receiver);
            ClientId = clientId;
        }

        [JsonPropertyName("type")][Key(2)] public string Type { get; set; } = "LeaveGamePacket";
    }

    [MessagePackObject]
    // [Document(StorageType = StorageType.Json, IndexName = "connections", Prefixes = new[] { "connections" })]
    public class RoomPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        [JsonPropertyName("id")]
        [Key(1)]

        public string Id { get; set; }

        [JsonPropertyName("maxCapacity")]
        [Key(2)]
        public uint MaxCapactiy { get; set; }

        [JsonPropertyName("connectionIds")]
        [Key(3)]
        public HashSet<string> ConnectionIds { get; set; }

        [JsonPropertyName("type")][Key(4)] public string Type { get; set; } = "RoomPacket";

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
    public static FailedPacket Failed(string reason, string receiver, string failType) => new FailedPacket("server", reason, failType, receiver);

    public static SuccessPacket Success(string message, string receiver, string successType)
    {
        return new SuccessPacket("server", message, successType, receiver);
    }
}