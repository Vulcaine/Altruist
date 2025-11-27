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

namespace Altruist
{
    public static class PacketHeaders
    {
        // Framework can later overwrite timestamp/receiver/etc as needed
        public static PacketHeader Broadcast = new PacketHeader
        {
            Sender = "server"
        };
    }

    // === Base Interfaces ===
    public interface IPacket : ITypedModel
    {
    }

    [MessagePackFormatter(typeof(PacketBaseFormatter))]
    public interface IPacketBase : IPacket
    {
        PacketHeader Header { get; set; }

        void Stamp(string sender, string receiver, DateTime receivedAt)
        {
            Header.Stamp(sender, receiver, receivedAt);
        }

        void SetReceiver(string clientId) => Header.SetReceiver(clientId);
        void SetTimestamp(DateTime receivedAt) => Header.SetTimestamp(receivedAt);
    }

    [MessagePackObject]
    public struct TextPacket : IPacketBase
    {
        // =============================
        // Header (framework will fill)
        // =============================
        [JsonPropertyName("header")]
        [Key(0)]
        public PacketHeader Header { get; set; }

        // =============================
        // Packet Text Content
        // =============================
        [JsonPropertyName("text")]
        [Key(1)]
        public string Text { get; set; }

        // =============================
        // Type Identifier
        // =============================
        [JsonPropertyName("type")]
        [Key(2)]
        public string Type { get; set; }

        // =============================
        // Constructors
        // =============================
        public TextPacket()
        {
            Header = default;      // framework fills these later
            Text = string.Empty;
            Type = nameof(TextPacket);
        }

        public TextPacket(string text)
        {
            Header = default;      // framework fills sender/receiver/timestamp
            Text = text;
            Type = nameof(TextPacket);
        }
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

        public DecodeType(string type)
        {
            Header = default;
            Type = type;
        }
    }

    [MessagePackObject]
    public struct InterprocessPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("processId")][Key(1)] public string ProcessId { get; set; }
        [JsonPropertyName("message")][Key(2)] public IPacketBase Message { get; set; }
        [JsonPropertyName("type")][Key(3)] public string Type { get; set; }

        public InterprocessPacket()
        {
            Header = default;
            ProcessId = "";
            Message = default!;
            Type = "InterprocessPacket";
        }

        public InterprocessPacket(string processId, IPacketBase message)
        {
            Header = default;
            ProcessId = processId;
            Message = message;
            Type = "InterprocessPacket";
        }
    }

    public interface IMovementPacket : IPacketBase
    {
    }

    // === Common Header Struct ===

    [MessagePackObject]
    public struct PacketHeader
    {
        // Framework will set this when sending
        [JsonPropertyName("header")][Key(0)] public long Timestamp { get; set; }

        [JsonPropertyName("receiver")][Key(1)] public string? Receiver { get; set; }
        [JsonPropertyName("sender")][Key(2)] public string Sender { get; set; }

        public void Stamp(string sender, string receiver, DateTime tt) => (Sender, Receiver, Timestamp) = (sender, receiver, tt.Ticks);
        public void SetReceiver(string clientId) => Receiver = clientId;
        public void SetTimestamp(DateTime tt) => Timestamp = tt.Ticks;
    }

    // === Packet Structs ===

    [MessagePackObject]
    public struct SyncPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        [JsonPropertyName("entityType")][Key(1)] public string EntityType { get; set; }
        [JsonPropertyName("data")][Key(2)] public Dictionary<string, object?> Data { get; set; }

        [JsonPropertyName("type")][Key(3)] public string Type { get; set; }

        public SyncPacket()
        {
            Header = default;
            EntityType = "";
            Data = new Dictionary<string, object?>();
            Type = "SyncPacket";
        }

        public SyncPacket(string entityType, Dictionary<string, object?> data)
        {
            Header = default;
            EntityType = entityType;
            Data = data;
            Type = "SyncPacket";
        }
    }

    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("event")][Key(1)] public string Event { get; set; }

        [JsonPropertyName("type")][Key(2)] public string Type { get; set; }

        public AltruistPacket()
        {
            Header = default;
            Event = "";
            Type = "AltruistPacket";
        }

        public AltruistPacket(string eventName)
        {
            Header = default;
            Event = eventName;
            Type = "AltruistPacket";
        }
    }

    public interface IResultPacket
    {

    }

    public interface IResultPacketWithPayload : IResultPacket
    {
        IPacketBase Payload { get; }
    }

    // Standardized success envelope:
    //  - Code (int)
    //  - Payload (actual packet)
    [MessagePackObject]
    public struct SuccessPacket : IPacketBase, IResultPacketWithPayload
    {
        [JsonPropertyName("header")]
        [Key(0)]
        public PacketHeader Header { get; set; }

        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        [JsonPropertyName("payload")]
        [Key(2)]
        public IPacketBase? Payload { get; set; }

        [JsonPropertyName("type")]
        [Key(3)]
        public string Type { get; set; }

        public SuccessPacket()
        {
            Header = default;
            Code = 0;
            Payload = default!;
            Type = "SuccessPacket";
        }

        public SuccessPacket(int code, IPacketBase? payload = null)
        {
            Header = default;
            Code = code;
            Payload = payload;
            Type = "SuccessPacket";
        }
    }

    // Standardized failure envelope:
    //  - Code (int)
    //  - Reason (string)
    //  - Payload (actual packet, optional context)
    [MessagePackObject]
    public struct FailedPacket : IPacketBase, IResultPacket
    {
        [JsonPropertyName("header")]
        [Key(0)]
        public PacketHeader Header { get; set; }

        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        [JsonPropertyName("reason")]
        [Key(2)]
        public string Reason { get; set; }

        [JsonPropertyName("type")]
        [Key(4)]
        public string Type { get; set; }

        public FailedPacket()
        {
            Header = default;
            Code = 0;
            Reason = "";
            Type = "FailedPacket";
        }

        public FailedPacket(int code, string reason)
        {
            Header = default;
            Code = code;
            Reason = reason;
            Type = "FailedPacket";
        }
    }

    [MessagePackObject]
    public struct ShootingPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

        [JsonPropertyName("type")][Key(1)] public string Type { get; set; }

        public ShootingPacket()
        {
            Header = default;
            Type = "ShootingPacket";
        }
    }

    [MessagePackObject]
    public struct ForwardMovementPacket : IMovementPacket
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("moveUp")][Key(1)] public bool MoveUp { get; set; }
        [JsonPropertyName("rotateLeft")][Key(2)] public bool RotateLeft { get; set; }
        [JsonPropertyName("rotateRight")][Key(3)] public bool RotateRight { get; set; }
        [JsonPropertyName("turbo")][Key(4)] public bool Turbo { get; set; }

        [JsonPropertyName("type")][Key(5)] public string Type { get; set; }

        public ForwardMovementPacket()
        {
            Header = default;
            MoveUp = false;
            RotateLeft = false;
            RotateRight = false;
            Turbo = false;
            Type = "ForwardMovementPacket";
        }

        public ForwardMovementPacket(bool moveUp, bool rotateLeft, bool rotateRight, bool turbo)
        {
            Header = default;
            MoveUp = moveUp;
            RotateLeft = rotateLeft;
            RotateRight = rotateRight;
            Turbo = turbo;
            Type = "ForwardMovementPacket";
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

        [JsonPropertyName("type")][Key(6)] public string Type { get; set; }

        public EightDirectionMovementPacket()
        {
            Header = default;
            MoveUp = false;
            MoveDown = false;
            MoveLeft = false;
            MoveRight = false;
            Turbo = false;
            Type = "EightDirectionMovementPacket";
        }

        public EightDirectionMovementPacket(bool moveUp, bool moveDown, bool moveLeft, bool moveRight, bool turbo)
        {
            Header = default;
            MoveUp = moveUp;
            MoveDown = moveDown;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            Turbo = turbo;
            Type = "EightDirectionMovementPacket";
        }
    }

    // === Helper Classes ===

    [MessagePackObject]
    public struct Vector2Message
    {
        [JsonPropertyName("x")][Key(0)] public float X { get; set; }
        [JsonPropertyName("y")][Key(1)] public float Y { get; set; }

        public Vector2Message()
        {
            X = 0;
            Y = 0;
        }

        public Vector2Message(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    [MessagePackObject]
    public struct HandshakeRequestPacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("type")][Key(1)] public string Type { get; set; }

        [JsonPropertyName("type")][Key(2)] public string Token { get; set; }

        public HandshakeRequestPacket()
        {
            Header = default;
            Type = "HandshakePacket";
            Token = "";
        }

        public HandshakeRequestPacket(string? token)
        {
            Header = default;
            Type = "HandshakePacket";
            Token = token ?? "";
        }
    }

    [MessagePackObject]
    public struct HandshakeResponsePacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("rooms")][Key(1)] public RoomPacket[] Rooms { get; set; }
        [JsonPropertyName("type")][Key(2)] public string Type { get; set; }

        public HandshakeResponsePacket()
        {
            Header = default;
            Rooms = Array.Empty<RoomPacket>();
            Type = "HandshakePacket";
        }

        public HandshakeResponsePacket(RoomPacket[] rooms)
        {
            Header = default;
            Rooms = rooms;
            Type = "HandshakePacket";
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
        [JsonPropertyName("type")][Key(5)] public string Type { get; set; }

        public JoinGamePacket()
        {
            Header = default;
            Name = string.Empty;
            RoomId = string.Empty;
            Position = new[] { 0f, 0f };
            WorldIndex = 0;
            Type = "JoinGamePacket";
        }

        public JoinGamePacket(string name, string? roomId = null, int? worldIndex = 0, float[]? position = null)
        {
            Header = default;
            Name = name;
            RoomId = roomId;
            Position = position ?? new[] { 0f, 0f };
            WorldIndex = worldIndex;
            Type = "JoinGamePacket";
        }
    }

    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }
        [JsonPropertyName("clientId")][Key(1)] public string ClientId { get; set; }

        [JsonPropertyName("type")][Key(2)] public string Type { get; set; }

        public LeaveGamePacket()
        {
            Header = default;
            ClientId = string.Empty;
            Type = "LeaveGamePacket";
        }

        public LeaveGamePacket(string clientId)
        {
            Header = default;
            ClientId = clientId;
            Type = "LeaveGamePacket";
        }
    }

    [MessagePackObject]
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

        [JsonPropertyName("type")][Key(4)] public string Type { get; set; }

        [IgnoreMember]
        public int PlayerCount => (ConnectionIds ?? new HashSet<string>()).Count;

        public RoomPacket()
        {
            Header = default;
            Id = string.Empty;
            MaxCapactiy = 100;
            ConnectionIds = new HashSet<string>();
            Type = "RoomPacket";
        }

        public RoomPacket(string roomId, uint maxCapacity = 100)
        {
            Header = default;
            Id = roomId;
            MaxCapactiy = maxCapacity;
            ConnectionIds = new HashSet<string>();
            Type = "RoomPacket";
        }

        public bool Has(string connectionId) => ConnectionIds.Contains(connectionId);

        public bool Full() => PlayerCount >= MaxCapactiy;

        public bool Empty() => PlayerCount == 0;

        public bool IsDefault() =>
            EqualityComparer<RoomPacket>.Default.Equals(this, default);

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

// === Broadcasting / result helpers ===

public sealed class RoomBroadcast
{
    public string RoomId { get; }
    public IPacketBase Packet { get; }

    public RoomBroadcast(string roomId, IPacketBase packet)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Packet = packet ?? throw new ArgumentNullException(nameof(packet));
    }
}

public static class ResultPacket
{
    public static SuccessPacket Success(int code, IPacketBase? payload = null)
        => new SuccessPacket(code, payload);

    public static SuccessPacket Success(int code, string message)
       => new SuccessPacket(code, new TextPacket(message));

    public static FailedPacket Failed(int code, string reason)
        => new FailedPacket(code, reason);
}
