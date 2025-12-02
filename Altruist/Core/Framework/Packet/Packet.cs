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

using Altruist.Networking.Codec.MessagePack;

using MessagePack;

namespace Altruist
{
    public static class PacketHeaders
    {
        // Framework can later overwrite timestamp/receiver/etc as needed
        public static readonly PacketHeader Broadcast = new PacketHeader
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
    }

    // === Common Header Struct (used only on envelope) ===

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

    // === Envelope (only place with header + type) ===

    [MessagePackObject]
    public struct MessageEnvelope
    {
        [Key(0)]
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [Key(1)]
        [JsonPropertyName("header")]
        public PacketHeader Header { get; set; }

        [Key(2)]
        [JsonPropertyName("message")]
        public IPacketBase Message { get; set; }

        public MessageEnvelope(PacketHeader header, IPacketBase message)
        {
            Header = header;
            Message = message;
            Type = message.GetType().Name;
        }

        public MessageEnvelope(IPacketBase message, string receiver)
        {
            Header = new PacketHeader
            {
                Sender = "server",
                Receiver = receiver
            };
            Message = message;
            Type = message.GetType().Name;
        }

        public void Stamp(string sender, string receiver, DateTime receivedAt)
            => Header.Stamp(sender, receiver, receivedAt);

        public void SetReceiver(string clientId)
            => Header.SetReceiver(clientId);

        public void SetTimestamp(DateTime receivedAt)
            => Header.SetTimestamp(receivedAt);
    }

    // === Simple Text Packet ===

    [MessagePackObject]
    public struct TextPacket : IPacketBase
    {
        [JsonPropertyName("text")]
        [Key(0)]
        public string Text { get; set; }

        public TextPacket()
        {
            Text = string.Empty;
        }

        public TextPacket(string text)
        {
            Text = text;
        }
    }

    // Used for interprocess communication: payload inside payload
    [MessagePackObject]
    public struct InterprocessPacket : IPacketBase
    {
        [JsonPropertyName("processId")]
        [Key(0)]
        public string ProcessId { get; set; }

        [JsonPropertyName("message")]
        [Key(1)]
        public IPacketBase Message { get; set; }

        public InterprocessPacket()
        {
            ProcessId = string.Empty;
            Message = default!;
        }

        public InterprocessPacket(string processId, IPacketBase message)
        {
            ProcessId = processId;
            Message = message;
        }
    }

    public interface IMovementPacket : IPacketBase
    {
    }

    // === Generic sync payload ===

    [MessagePackObject]
    public struct SyncPacket : IPacketBase
    {
        [JsonPropertyName("entityType")]
        [Key(0)]
        public string EntityType { get; set; }

        [JsonPropertyName("data")]
        [Key(1)]
        public Dictionary<string, object?> Data { get; set; }

        public SyncPacket()
        {
            EntityType = string.Empty;
            Data = new Dictionary<string, object?>();
        }

        public SyncPacket(string entityType, Dictionary<string, object?> data)
        {
            EntityType = entityType;
            Data = data ?? new Dictionary<string, object?>();
        }
    }

    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        [JsonPropertyName("event")]
        [Key(0)]
        public string Event { get; set; }

        public AltruistPacket()
        {
            Event = string.Empty;
        }

        public AltruistPacket(string eventName)
        {
            Event = eventName;
        }
    }

    // === Result packets (used as payload inside envelope) ===

    public interface IResultPacket
    {
    }

    public interface IResultPacketWithPayload : IResultPacket
    {
        IPacketBase? Payload { get; }
    }

    // Standardized success payload:
    //  - Code (int)
    //  - Payload (actual packet)
    [MessagePackObject]
    public struct SuccessPacket : IPacketBase, IResultPacketWithPayload
    {
        [JsonPropertyName("code")]
        [Key(0)]
        public int Code { get; set; }

        [JsonPropertyName("payload")]
        [Key(1)]
        public IPacketBase? Payload { get; set; }

        public SuccessPacket()
        {
            Code = 0;
            Payload = default!;
        }

        public SuccessPacket(int code, IPacketBase? payload = null)
        {
            Code = code;
            Payload = payload;
        }
    }

    // Standardized failure payload:
    //  - Code (int)
    //  - Reason (string)
    [MessagePackObject]
    public struct FailedPacket : IPacketBase, IResultPacket
    {
        [JsonPropertyName("code")]
        [Key(0)]
        public int Code { get; set; }

        [JsonPropertyName("reason")]
        [Key(1)]
        public string Reason { get; set; }

        public FailedPacket()
        {
            Code = 0;
            Reason = string.Empty;
        }

        public FailedPacket(int code, string reason)
        {
            Code = code;
            Reason = reason;
        }
    }

    // === Game payloads (no header/type here; envelope carries those) ===

    [MessagePackObject]
    public struct ShootingPacket : IPacketBase
    {
        // Add shooting-specific fields when needed
    }

    [MessagePackObject]
    public struct ForwardMovementPacket : IMovementPacket
    {
        [JsonPropertyName("moveUp")]
        [Key(0)]
        public bool MoveUp { get; set; }

        [JsonPropertyName("rotateLeft")]
        [Key(1)]
        public bool RotateLeft { get; set; }

        [JsonPropertyName("rotateRight")]
        [Key(2)]
        public bool RotateRight { get; set; }

        [JsonPropertyName("turbo")]
        [Key(3)]
        public bool Turbo { get; set; }

        public ForwardMovementPacket(bool moveUp, bool rotateLeft, bool rotateRight, bool turbo)
        {
            MoveUp = moveUp;
            RotateLeft = rotateLeft;
            RotateRight = rotateRight;
            Turbo = turbo;
        }
    }

    [MessagePackObject]
    public struct EightDirectionMovementPacket : IMovementPacket
    {
        [JsonPropertyName("moveUp")]
        [Key(0)]
        public bool MoveUp { get; set; }

        [JsonPropertyName("moveDown")]
        [Key(1)]
        public bool MoveDown { get; set; }

        [JsonPropertyName("moveLeft")]
        [Key(2)]
        public bool MoveLeft { get; set; }

        [JsonPropertyName("moveRight")]
        [Key(3)]
        public bool MoveRight { get; set; }

        [JsonPropertyName("turbo")]
        [Key(4)]
        public bool Turbo { get; set; }

        public EightDirectionMovementPacket(bool moveUp, bool moveDown, bool moveLeft, bool moveRight, bool turbo)
        {
            MoveUp = moveUp;
            MoveDown = moveDown;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            Turbo = turbo;
        }
    }

    // === Helper DTOs ===

    [MessagePackObject]
    public struct Vector2Message
    {
        [JsonPropertyName("x")]
        [Key(0)]
        public float X { get; set; }

        [JsonPropertyName("y")]
        [Key(1)]
        public float Y { get; set; }

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
        [JsonPropertyName("token")]
        [Key(0)]
        public string Token { get; set; }

        public HandshakeRequestPacket()
        {
            Token = string.Empty;
        }

        public HandshakeRequestPacket(string? token)
        {
            Token = token ?? string.Empty;
        }
    }

    [MessagePackObject]
    public struct HandshakeResponsePacket : IPacketBase
    {
        [JsonPropertyName("rooms")]
        [Key(0)]
        public RoomPacket[] Rooms { get; set; }

        public HandshakeResponsePacket()
        {
            Rooms = Array.Empty<RoomPacket>();
        }

        public HandshakeResponsePacket(RoomPacket[] rooms)
        {
            Rooms = rooms ?? Array.Empty<RoomPacket>();
        }
    }

    [MessagePackObject]
    public struct JoinGamePacket : IPacketBase
    {
        [JsonPropertyName("name")]
        [Key(0)]
        public string Name { get; set; }

        [JsonPropertyName("roomId")]
        [Key(1)]
        public string? RoomId { get; set; }

        [JsonPropertyName("world")]
        [Key(2)]
        public int? WorldIndex { get; set; }

        [JsonPropertyName("position")]
        [Key(3)]
        public float[]? Position { get; set; }

        public JoinGamePacket()
        {
            Name = string.Empty;
            RoomId = string.Empty;
            Position = [0f, 0f];
            WorldIndex = 0;
        }

        public JoinGamePacket(string name, string? roomId = null, int? worldIndex = 0, float[]? position = null)
        {
            Name = name;
            RoomId = roomId;
            Position = position ?? [0f, 0f];
            WorldIndex = worldIndex;
        }
    }

    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        [JsonPropertyName("clientId")]
        [Key(0)]
        public string ClientId { get; set; }

        public LeaveGamePacket()
        {
            ClientId = string.Empty;
        }

        public LeaveGamePacket(string clientId)
        {
            ClientId = clientId;
        }
    }

    [MessagePackObject]
    public class RoomPacket : IPacketBase
    {
        [JsonPropertyName("id")]
        [Key(0)]
        public string Id { get; set; }

        [JsonPropertyName("maxCapacity")]
        [Key(1)]
        public uint MaxCapactiy { get; set; }

        [JsonPropertyName("connectionIds")]
        [Key(2)]
        public HashSet<string> ConnectionIds { get; set; }

        [IgnoreMember]
        public int PlayerCount => (ConnectionIds ?? new HashSet<string>()).Count;

        public RoomPacket()
        {
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
    public Altruist.IPacketBase Packet { get; }

    public RoomBroadcast(string roomId, Altruist.IPacketBase packet)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Packet = packet ?? throw new ArgumentNullException(nameof(packet));
    }
}

public static class ResultPacket
{
    public static Altruist.SuccessPacket Success(int code, Altruist.IPacketBase? payload = null)
        => new Altruist.SuccessPacket(code, payload);

    public static Altruist.SuccessPacket Success(int code, string message)
        => new Altruist.SuccessPacket(code, new Altruist.TextPacket(message));

    public static Altruist.FailedPacket Failed(int code, string reason)
        => new Altruist.FailedPacket(code, reason);
}
