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
    /// <summary>
    /// Reserved message codes for built-in framework packets.
    /// User-defined packets are recommended to start from 1000+.
    /// </summary>
    public static class PacketCodes
    {
        // 0 reserved / invalid
        public const uint Text = 1;
        public const uint Interprocess = 2;
        public const uint Sync = 3;
        public const uint Altruist = 4;

        public const uint Success = 5;
        public const uint Failed = 6;

        public const uint HandshakeRequest = 7;
        public const uint HandshakeResponse = 8;

        public const uint JoinGame = 9;
        public const uint LeaveGame = 10;

        public const uint Room = 11;
    }

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

    /// <summary>
    /// Base packet interface. All packets MUST serialize MessageCode at Key(0),
    /// and shift their own fields to start at Key(1).
    /// </summary>
    [MessagePackFormatter(typeof(PacketBaseFormatter))]
    public interface IPacketBase : IPacket
    {
        // NOTE: Do NOT put [Key] here; Key indices are defined by the concrete packet types.
        uint MessageCode { get; set; }
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
        public object? Message { get; set; }

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
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("text")]
        [Key(1)]
        public string Text { get; set; }

        public TextPacket()
        {
            MessageCode = PacketCodes.Text;
            Text = string.Empty;
        }

        public TextPacket(string text)
        {
            MessageCode = PacketCodes.Text;
            Text = text;
        }
    }

    // Used for interprocess communication: payload inside payload
    [MessagePackObject]
    public struct InterprocessPacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("processId")]
        [Key(1)]
        public string ProcessId { get; set; }

        [JsonPropertyName("message")]
        [Key(2)]
        public IPacketBase Message { get; set; }

        public InterprocessPacket()
        {
            MessageCode = PacketCodes.Interprocess;
            ProcessId = string.Empty;
            Message = default!;
        }

        public InterprocessPacket(string processId, IPacketBase message)
        {
            MessageCode = PacketCodes.Interprocess;
            ProcessId = processId;
            Message = message;
        }
    }

    // === Generic sync payload ===

    [MessagePackObject]
    public struct SyncPacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("entityType")]
        [Key(1)]
        public string EntityType { get; set; }

        [JsonPropertyName("data")]
        [Key(2)]
        public Dictionary<string, object?> Data { get; set; }

        public SyncPacket()
        {
            MessageCode = PacketCodes.Sync;
            EntityType = string.Empty;
            Data = new Dictionary<string, object?>();
        }

        public SyncPacket(string entityType, Dictionary<string, object?> data)
        {
            MessageCode = PacketCodes.Sync;
            EntityType = entityType;
            Data = data ?? new Dictionary<string, object?>();
        }
    }

    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("event")]
        [Key(1)]
        public string Event { get; set; }

        public AltruistPacket()
        {
            MessageCode = PacketCodes.Altruist;
            Event = string.Empty;
        }

        public AltruistPacket(string eventName)
        {
            MessageCode = PacketCodes.Altruist;
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
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        [JsonPropertyName("payload")]
        [Key(2)]
        public IPacketBase? Payload { get; set; }

        public SuccessPacket()
        {
            MessageCode = PacketCodes.Success;
            Code = 0;
            Payload = default!;
        }

        public SuccessPacket(int code, IPacketBase? payload = null)
        {
            MessageCode = PacketCodes.Success;
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
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        [JsonPropertyName("reason")]
        [Key(2)]
        public string Reason { get; set; }

        public FailedPacket()
        {
            MessageCode = PacketCodes.Failed;
            Code = 0;
            Reason = string.Empty;
        }

        public FailedPacket(int code, string reason)
        {
            MessageCode = PacketCodes.Failed;
            Code = code;
            Reason = reason;
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
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("token")]
        [Key(1)]
        public string Token { get; set; }

        public HandshakeRequestPacket()
        {
            MessageCode = PacketCodes.HandshakeRequest;
            Token = string.Empty;
        }

        public HandshakeRequestPacket(string? token)
        {
            MessageCode = PacketCodes.HandshakeRequest;
            Token = token ?? string.Empty;
        }
    }

    [MessagePackObject]
    public struct HandshakeResponsePacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("rooms")]
        [Key(1)]
        public RoomPacket[] Rooms { get; set; }

        public HandshakeResponsePacket()
        {
            MessageCode = PacketCodes.HandshakeResponse;
            Rooms = Array.Empty<RoomPacket>();
        }

        public HandshakeResponsePacket(RoomPacket[] rooms)
        {
            MessageCode = PacketCodes.HandshakeResponse;
            Rooms = rooms ?? Array.Empty<RoomPacket>();
        }
    }

    [MessagePackObject]
    public struct JoinGamePacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("name")]
        [Key(1)]
        public string Name { get; set; }

        [JsonPropertyName("roomId")]
        [Key(2)]
        public string? RoomId { get; set; }

        [JsonPropertyName("world")]
        [Key(3)]
        public int? WorldIndex { get; set; }

        [JsonPropertyName("position")]
        [Key(4)]
        public float[]? Position { get; set; }

        public JoinGamePacket()
        {
            MessageCode = PacketCodes.JoinGame;
            Name = string.Empty;
            RoomId = string.Empty;
            Position = new[] { 0f, 0f };
            WorldIndex = 0;
        }

        public JoinGamePacket(string name, string? roomId = null, int? worldIndex = 0, float[]? position = null)
        {
            MessageCode = PacketCodes.JoinGame;
            Name = name;
            RoomId = roomId;
            Position = position ?? new[] { 0f, 0f };
            WorldIndex = worldIndex;
        }
    }

    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("clientId")]
        [Key(1)]
        public string ClientId { get; set; }

        public LeaveGamePacket()
        {
            MessageCode = PacketCodes.LeaveGame;
            ClientId = string.Empty;
        }

        public LeaveGamePacket(string clientId)
        {
            MessageCode = PacketCodes.LeaveGame;
            ClientId = clientId;
        }
    }

    [MessagePackObject]
    public class RoomPacket : IPacketBase
    {
        [Key(0)]
        public uint MessageCode { get; set; } = PacketCodes.Room;

        [JsonPropertyName("id")]
        [Key(1)]
        public string Id { get; set; }

        [JsonPropertyName("maxCapacity")]
        [Key(2)]
        public uint MaxCapactiy { get; set; }

        [JsonPropertyName("connectionIds")]
        [Key(3)]
        public HashSet<string> ConnectionIds { get; set; }

        [IgnoreMember]
        public int PlayerCount => (ConnectionIds ?? new HashSet<string>()).Count;

        public RoomPacket()
        {
            MessageCode = PacketCodes.Room;
            Id = string.Empty;
            MaxCapactiy = 100;
            ConnectionIds = new HashSet<string>();
        }

        public RoomPacket(string roomId, uint maxCapacity = 100)
        {
            MessageCode = PacketCodes.Room;
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
            // NOTE: RoomPacket is a class; this mutates the same instance.
            ConnectionIds.Add(connectionId);
            return this;
        }

        public RoomPacket RemoveConnection(string connectionId)
        {
            // NOTE: RoomPacket is a class; this mutates the same instance.
            ConnectionIds.Remove(connectionId);
            return this;
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
