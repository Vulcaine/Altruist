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

using MessagePack;

// NOTE: PacketCodes, PacketHeaders, IPacket, IPacketBase, PacketHeader, MessageEnvelope
// have moved to the Altruist.Protocol package (still under namespace Altruist) so the
// client (Altruist.Client) and server can share one source of truth for the wire shape.
// This file keeps the *concrete* framework packets (Text, Interprocess, Sync, etc.)
// that depend on Core types.

namespace Altruist
{
    // === Simple Text Packet ===

    /// <summary>A built-in packet carrying a single string (<see cref="PacketCodes.Text"/>). Use it for
    /// plain text messages and human-readable result payloads (<see cref="ResultPacket.Success(int,string)"/>
    /// wraps its message in one). For structured data define your own <see cref="IPacketBase"/> type.</summary>
    [MessagePackObject]
    public struct TextPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>The text content (JSON <c>text</c>).</summary>
        [JsonPropertyName("text")]
        [Key(1)]
        public string Text { get; set; }

        /// <summary>An empty text packet with <see cref="PacketCodes.Text"/> set.</summary>
        public TextPacket()
        {
            MessageCode = PacketCodes.Text;
            Text = string.Empty;
        }

        /// <summary>A text packet carrying <paramref name="text"/>.</summary>
        public TextPacket(string text)
        {
            MessageCode = PacketCodes.Text;
            Text = text;
        }
    }

    // Used for interprocess communication: payload inside payload
    /// <summary>Framework-internal wrapper for forwarding a packet between server processes
    /// (<see cref="PacketCodes.Interprocess"/>): when the target client is not connected to this process,
    /// the Redis router wraps the packet with the sender's process id and pushes it onto the shared queue
    /// for the owning process to deliver. Application code normally does not create these.</summary>
    [MessagePackObject]
    public struct InterprocessPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>Id of the process that forwarded the packet (JSON <c>processId</c>).</summary>
        [JsonPropertyName("processId")]
        [Key(1)]
        public string ProcessId { get; set; }

        /// <summary>The wrapped packet to deliver (JSON <c>message</c>); serialized polymorphically through
        /// <see cref="IPacketBase"/>.</summary>
        [JsonPropertyName("message")]
        [Key(2)]
        public IPacketBase Message { get; set; }

        /// <summary>An empty interprocess packet with <see cref="PacketCodes.Interprocess"/> set.</summary>
        public InterprocessPacket()
        {
            MessageCode = PacketCodes.Interprocess;
            ProcessId = string.Empty;
            Message = default!;
        }

        /// <summary>Wraps <paramref name="message"/> as forwarded by process <paramref name="processId"/>.</summary>
        public InterprocessPacket(string processId, IPacketBase message)
        {
            MessageCode = PacketCodes.Interprocess;
            ProcessId = processId;
            Message = message;
        }
    }

    // === Generic sync payload ===

    /// <summary>Generic entity state-sync payload (<see cref="PacketCodes.Sync"/>): the entity's type name
    /// plus a property-name → value map of what changed. Sent by the framework's entity sync/broadcast
    /// paths; the dictionary values are serialized as untyped objects, so prefer a dedicated packet type
    /// for high-frequency or size-sensitive state.</summary>
    [MessagePackObject]
    public struct SyncPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>Entity type name (the framework passes <c>entity.GetType().Name</c>; JSON <c>entityType</c>).</summary>
        [JsonPropertyName("entityType")]
        [Key(1)]
        public string EntityType { get; set; }

        /// <summary>Changed property values keyed by property name (JSON <c>data</c>).</summary>
        [JsonPropertyName("data")]
        [Key(2)]
        public Dictionary<string, object?> Data { get; set; }

        /// <summary>An empty sync packet with <see cref="PacketCodes.Sync"/> set.</summary>
        public SyncPacket()
        {
            MessageCode = PacketCodes.Sync;
            EntityType = string.Empty;
            Data = new Dictionary<string, object?>();
        }

        /// <summary>A sync packet for <paramref name="entityType"/>; a null <paramref name="data"/> becomes an
        /// empty dictionary.</summary>
        public SyncPacket(string entityType, Dictionary<string, object?> data)
        {
            MessageCode = PacketCodes.Sync;
            EntityType = entityType;
            Data = data ?? new Dictionary<string, object?>();
        }
    }

    /// <summary>The inbound event envelope (<see cref="PacketCodes.Altruist"/>): names the event a client
    /// sent so the connection manager can route it to the matching handler. JSON clients send
    /// <c>{"event": "name", "data": {...}}</c>; binary codecs use event-prefixed framing
    /// (<c>[1-byte length][UTF-8 event name][payload]</c>), from which the framework builds this packet.</summary>
    [MessagePackObject]
    public struct AltruistPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>The event name used for handler routing (JSON <c>event</c>).</summary>
        [JsonPropertyName("event")]
        [Key(1)]
        public string Event { get; set; }

        /// <summary>
        /// Raw packet bytes from the transport layer. Available for protocols that need
        /// access to variable-length data beyond the fixed struct header (e.g. chat messages).
        /// Null for protocols that don't set it.
        /// </summary>
        [IgnoreMember]
        [JsonIgnore]
        public byte[]? RawData { get; set; }

        /// <summary>An empty event packet with <see cref="PacketCodes.Altruist"/> set.</summary>
        public AltruistPacket()
        {
            MessageCode = PacketCodes.Altruist;
            Event = string.Empty;
            RawData = null;
        }

        /// <summary>An event packet for <paramref name="eventName"/>.</summary>
        public AltruistPacket(string eventName)
        {
            MessageCode = PacketCodes.Altruist;
            Event = eventName;
            RawData = null;
        }
    }

    // === Result packets (used as payload inside envelope) ===

    /// <summary>Marker for the result of a framework request handler (handshake, join game): either a
    /// <see cref="SuccessPacket"/> or a <see cref="FailedPacket"/>. Create them with
    /// <see cref="ResultPacket"/>. Note: the session portal only sends results that implement
    /// <see cref="IResultPacketWithPayload"/>, and then only the payload itself, so a
    /// <see cref="FailedPacket"/> returned from such a handler is not delivered to the client.</summary>
    public interface IResultPacket
    {
    }

    /// <summary>A result that carries a packet to deliver back to the requesting client.</summary>
    public interface IResultPacketWithPayload : IResultPacket
    {
        /// <summary>The packet to send to the client, or null for nothing.</summary>
        IPacketBase? Payload { get; }
    }

    // Standardized success payload:
    //  - Code (int)
    //  - Payload (actual packet)
    /// <summary>Standard success result (<see cref="PacketCodes.Success"/>): a <see cref="TransportCode"/>
    /// plus an optional payload packet. Prefer <see cref="ResultPacket.Success(int,IPacketBase)"/> to build
    /// one.</summary>
    [MessagePackObject]
    public struct SuccessPacket : IPacketBase, IResultPacketWithPayload
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>A <see cref="TransportCode"/> value (JSON <c>code</c>).</summary>
        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        /// <summary>The packet returned to the client, if any (JSON <c>payload</c>).</summary>
        [JsonPropertyName("payload")]
        [Key(2)]
        public IPacketBase? Payload { get; set; }

        /// <summary>An empty success packet (code 0, no payload).</summary>
        public SuccessPacket()
        {
            MessageCode = PacketCodes.Success;
            Code = 0;
            Payload = default!;
        }

        /// <summary>A success result with <paramref name="code"/> and an optional <paramref name="payload"/>.</summary>
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
    /// <summary>Standard failure result (<see cref="PacketCodes.Failed"/>): a <see cref="TransportCode"/>
    /// plus a human-readable reason. Prefer <see cref="ResultPacket.Failed(int,string)"/> to build one.</summary>
    [MessagePackObject]
    public struct FailedPacket : IPacketBase, IResultPacket
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>A <see cref="TransportCode"/> value, usually 4xx/5xx (JSON <c>code</c>).</summary>
        [JsonPropertyName("code")]
        [Key(1)]
        public int Code { get; set; }

        /// <summary>Why the request failed (JSON <c>reason</c>).</summary>
        [JsonPropertyName("reason")]
        [Key(2)]
        public string Reason { get; set; }

        /// <summary>An empty failure packet (code 0, empty reason).</summary>
        public FailedPacket()
        {
            MessageCode = PacketCodes.Failed;
            Code = 0;
            Reason = string.Empty;
        }

        /// <summary>A failure result with <paramref name="code"/> and <paramref name="reason"/>.</summary>
        public FailedPacket(int code, string reason)
        {
            MessageCode = PacketCodes.Failed;
            Code = code;
            Reason = reason;
        }
    }

    // === Helper DTOs ===

    /// <summary>A serializable 2D vector DTO for packets (JSON <c>{x, y}</c>, MessagePack keys 0/1). Use it
    /// in wire types instead of <see cref="System.Numerics.Vector2"/>, which has no MessagePack/JSON
    /// attributes.</summary>
    [MessagePackObject]
    public struct Vector2Message
    {
        /// <summary>X component (JSON <c>x</c>).</summary>
        [JsonPropertyName("x")]
        [Key(0)]
        public float X { get; set; }

        /// <summary>Y component (JSON <c>y</c>).</summary>
        [JsonPropertyName("y")]
        [Key(1)]
        public float Y { get; set; }

        /// <summary>(0, 0).</summary>
        public Vector2Message()
        {
            X = 0;
            Y = 0;
        }

        /// <summary>Creates a vector from its components.</summary>
        public Vector2Message(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>Client → server session handshake (<see cref="PacketCodes.HandshakeRequest"/>) carrying an
    /// optional auth token. Answered with a <see cref="HandshakeResponsePacket"/> listing the rooms.</summary>
    [MessagePackObject]
    public struct HandshakeRequestPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>The client's auth token, or empty when none (JSON <c>token</c>).</summary>
        [JsonPropertyName("token")]
        [Key(1)]
        public string Token { get; set; }

        /// <summary>A handshake request with no token.</summary>
        public HandshakeRequestPacket()
        {
            MessageCode = PacketCodes.HandshakeRequest;
            Token = string.Empty;
        }

        /// <summary>A handshake request with <paramref name="token"/> (null becomes empty).</summary>
        public HandshakeRequestPacket(string? token)
        {
            MessageCode = PacketCodes.HandshakeRequest;
            Token = token ?? string.Empty;
        }
    }

    /// <summary>Server → client handshake answer (<see cref="PacketCodes.HandshakeResponse"/>) listing the
    /// available rooms.</summary>
    [MessagePackObject]
    public struct HandshakeResponsePacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>The rooms the client can join (JSON <c>rooms</c>).</summary>
        [JsonPropertyName("rooms")]
        [Key(1)]
        public RoomPacket[] Rooms { get; set; }

        /// <summary>An empty response (no rooms).</summary>
        public HandshakeResponsePacket()
        {
            MessageCode = PacketCodes.HandshakeResponse;
            Rooms = Array.Empty<RoomPacket>();
        }

        /// <summary>A response listing <paramref name="rooms"/> (null becomes empty).</summary>
        public HandshakeResponsePacket(RoomPacket[] rooms)
        {
            MessageCode = PacketCodes.HandshakeResponse;
            Rooms = rooms ?? Array.Empty<RoomPacket>();
        }
    }

    /// <summary>Client → server request to join the game (<see cref="PacketCodes.JoinGame"/>).</summary>
    [MessagePackObject]
    public struct JoinGamePacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>Display name of the joining player (JSON <c>name</c>).</summary>
        [JsonPropertyName("name")]
        [Key(1)]
        public string Name { get; set; }

        /// <summary>Room to join, or null/empty to let the server choose (JSON <c>roomId</c>).</summary>
        [JsonPropertyName("roomId")]
        [Key(2)]
        public string? RoomId { get; set; }

        /// <summary>Index of the world to spawn in (JSON <c>world</c>).</summary>
        [JsonPropertyName("world")]
        [Key(3)]
        public int? WorldIndex { get; set; }

        /// <summary>Requested spawn position as a coordinate array (defaults to <c>[0, 0]</c>; JSON
        /// <c>position</c>).</summary>
        [JsonPropertyName("position")]
        [Key(4)]
        public float[]? Position { get; set; }

        /// <summary>An empty request: empty name, empty room id, world 0, position <c>[0, 0]</c>.</summary>
        public JoinGamePacket()
        {
            MessageCode = PacketCodes.JoinGame;
            Name = string.Empty;
            RoomId = string.Empty;
            Position = new[] { 0f, 0f };
            WorldIndex = 0;
        }

        /// <summary>A join request. Note: <paramref name="roomId"/> defaults to null here, whereas the
        /// parameterless constructor sets it to an empty string.</summary>
        public JoinGamePacket(string name, string? roomId = null, int? worldIndex = 0, float[]? position = null)
        {
            MessageCode = PacketCodes.JoinGame;
            Name = name;
            RoomId = roomId;
            Position = position ?? new[] { 0f, 0f };
            WorldIndex = worldIndex;
        }
    }

    /// <summary>Request to leave the game (<see cref="PacketCodes.LeaveGame"/>); also broadcast to the room
    /// so the remaining clients learn who left.</summary>
    [MessagePackObject]
    public struct LeaveGamePacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>The connection id of the leaving client (JSON <c>clientId</c>).</summary>
        [JsonPropertyName("clientId")]
        [Key(1)]
        public string ClientId { get; set; }

        /// <summary>An empty leave packet.</summary>
        public LeaveGamePacket()
        {
            MessageCode = PacketCodes.LeaveGame;
            ClientId = string.Empty;
        }

        /// <summary>A leave packet for <paramref name="clientId"/>.</summary>
        public LeaveGamePacket(string clientId)
        {
            MessageCode = PacketCodes.LeaveGame;
            ClientId = clientId;
        }
    }

    /// <summary>A room: an id, a capacity and the connection ids inside it (<see cref="PacketCodes.Room"/>).
    /// A class (reference type), so <see cref="AddConnection"/> / <see cref="RemoveConnection"/> mutate the
    /// instance they are called on.</summary>
    [MessagePackObject]
    public class RoomPacket : IPacketBase
    {
        /// <inheritdoc/>
        [Key(0)]
        public uint MessageCode { get; set; } = PacketCodes.Room;

        /// <summary>Room id (JSON <c>id</c>).</summary>
        [JsonPropertyName("id")]
        [Key(1)]
        public string Id { get; set; }

        /// <summary>Maximum number of connections (default 100; JSON <c>maxCapacity</c>). The property name is
        /// misspelled (<c>MaxCapactiy</c>) in code; the wire name is correct.</summary>
        [JsonPropertyName("maxCapacity")]
        [Key(2)]
        public uint MaxCapactiy { get; set; }

        /// <summary>Connection ids currently in the room (JSON <c>connectionIds</c>).</summary>
        [JsonPropertyName("connectionIds")]
        [Key(3)]
        public HashSet<string> ConnectionIds { get; set; }

        /// <summary>Number of connections in the room (not serialized).</summary>
        [IgnoreMember]
        public int PlayerCount => (ConnectionIds ?? new HashSet<string>()).Count;

        /// <summary>An empty room with capacity 100.</summary>
        public RoomPacket()
        {
            MessageCode = PacketCodes.Room;
            Id = string.Empty;
            MaxCapactiy = 100;
            ConnectionIds = new HashSet<string>();
        }

        /// <summary>An empty room <paramref name="roomId"/> with <paramref name="maxCapacity"/>.</summary>
        public RoomPacket(string roomId, uint maxCapacity = 100)
        {
            MessageCode = PacketCodes.Room;
            Id = roomId;
            MaxCapactiy = maxCapacity;
            ConnectionIds = new HashSet<string>();
        }

        /// <summary>Whether <paramref name="connectionId"/> is in the room.</summary>
        public bool Has(string connectionId) => ConnectionIds.Contains(connectionId);

        /// <summary>Whether the room is at or over capacity.</summary>
        public bool Full() => PlayerCount >= MaxCapactiy;

        /// <summary>Whether the room has no connections.</summary>
        public bool Empty() => PlayerCount == 0;

        /// <summary>Compares this instance with <c>default</c> (null for a class), so it is always false on a
        /// live instance.</summary>
        public bool IsDefault() =>
            EqualityComparer<RoomPacket>.Default.Equals(this, default);

        /// <summary>Adds <paramref name="connectionId"/> (mutates this instance) and returns it for chaining.</summary>
        public RoomPacket AddConnection(string connectionId)
        {
            // NOTE: RoomPacket is a class; this mutates the same instance.
            ConnectionIds.Add(connectionId);
            return this;
        }

        /// <summary>Removes <paramref name="connectionId"/> (mutates this instance) and returns it for chaining.</summary>
        public RoomPacket RemoveConnection(string connectionId)
        {
            // NOTE: RoomPacket is a class; this mutates the same instance.
            ConnectionIds.Remove(connectionId);
            return this;
        }

        /// <summary>Formats as <c>Room[id]: count/capacity</c>.</summary>
        public override string ToString()
        {
            return $"Room[{Id}]: {PlayerCount}/{MaxCapactiy}";
        }
    }
}

// === Broadcasting / result helpers ===

/// <summary>A result that sends <see cref="Packet"/> to every connection in room <see cref="RoomId"/>
/// (used for leave-game notifications; the session portal publishes it through the room router).</summary>
public sealed class RoomBroadcast
{
    /// <summary>The target room id.</summary>
    public string RoomId { get; }
    /// <summary>The packet to broadcast.</summary>
    public Altruist.IPacketBase Packet { get; }

    /// <summary>Creates a room broadcast.</summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="roomId"/> or <paramref name="packet"/> is null.</exception>
    public RoomBroadcast(string roomId, Altruist.IPacketBase packet)
    {
        RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
        Packet = packet ?? throw new ArgumentNullException(nameof(packet));
    }
}

/// <summary>Factory for <see cref="Altruist.IResultPacket"/> values returned by framework request
/// handlers, with codes from <see cref="Altruist.TransportCode"/>.</summary>
/// <example><code>
/// if (room.Full()) return ResultPacket.Failed(TransportCode.Conflict, "Room is full.");
/// return ResultPacket.Success(TransportCode.Ok, new JoinedPacket(...));
/// </code></example>
public static class ResultPacket
{
    /// <summary>A success result with <paramref name="code"/> and an optional <paramref name="payload"/>
    /// (the packet actually delivered to the client).</summary>
    public static Altruist.SuccessPacket Success(int code, Altruist.IPacketBase? payload = null)
        => new Altruist.SuccessPacket(code, payload);

    /// <summary>A success result whose payload is a <see cref="Altruist.TextPacket"/> carrying
    /// <paramref name="message"/>.</summary>
    public static Altruist.SuccessPacket Success(int code, string message)
        => new Altruist.SuccessPacket(code, new Altruist.TextPacket(message));

    /// <summary>A failure result with <paramref name="code"/> and <paramref name="reason"/>.</summary>
    public static Altruist.FailedPacket Failed(int code, string reason)
        => new Altruist.FailedPacket(code, reason);
}
