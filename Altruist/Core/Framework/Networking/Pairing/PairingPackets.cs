/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;

namespace Altruist.Networking;

/// <summary>
/// Wire message codes (<see cref="IPacketBase.MessageCode"/>) of device pairing, range 7001-7009.
/// Gates: <c>pair-host</c>, <c>pair-join</c>, <c>pair-send</c>, <c>pair-ping</c>. Avoid these values for your own packets.
/// The TypeScript twin is <c>@altruist/client</c> pairing.
/// </summary>
public static class PairingPacketCodes
{
    /// <summary>Client to server: <see cref="PairingHostPacket"/>.</summary>
    public const uint Host = 7001;
    /// <summary>Client to server: <see cref="PairingJoinPacket"/>.</summary>
    public const uint Join = 7002;
    /// <summary>Client to server: <see cref="PairingSendPacket"/>.</summary>
    public const uint Send = 7003;
    /// <summary>Server to client: <see cref="PairingHostedPacket"/>.</summary>
    public const uint Hosted = 7004;
    /// <summary>Server to client: <see cref="PairingJoinedPacket"/>.</summary>
    public const uint Joined = 7005;
    /// <summary>Server to client: <see cref="PairingPeerPacket"/>.</summary>
    public const uint Peer = 7006;
    /// <summary>Server to client: <see cref="PairingMessagePacket"/>.</summary>
    public const uint Message = 7007;
    /// <summary>Server to client: <see cref="PairingRejectedPacket"/>.</summary>
    public const uint Rejected = 7008;
    /// <summary>Client to server: <see cref="PairingPingPacket"/>.</summary>
    public const uint Ping = 7009;
}

/// <summary>Client request to open a pairing session as its host (gate <c>pair-host</c>).</summary>
[MessagePackObject]
public class PairingHostPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Host"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Host;
}

/// <summary>Client request to join a session as a companion (gate <c>pair-join</c>).</summary>
[MessagePackObject]
public class PairingJoinPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Join"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Join;
    /// <summary>The host's code (case-insensitive).</summary>
    [Key(1)] public string Code { get; set; } = "";
}

/// <summary>Client payload for a peer of its session (gate <c>pair-send</c>): host to a companion, or a companion to the host.</summary>
[MessagePackObject]
public class PairingSendPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Send"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Send;
    /// <summary>Target slot: 0 = the host, 1.. = a companion.</summary>
    [Key(1)] public int To { get; set; }
    /// <summary>Opaque bytes, at most <see cref="PairingOptions.MaxPayloadBytes"/>.</summary>
    [Key(2)] public byte[] Payload { get; set; } = Array.Empty<byte>();
}

/// <summary>The session is open: show <see cref="Code"/> to the player.</summary>
[MessagePackObject]
public class PairingHostedPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Hosted"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Hosted;
    /// <summary>The code companions join with.</summary>
    [Key(1)] public string Code { get; set; } = "";
    /// <summary>How many companions may join.</summary>
    [Key(2)] public int MaxCompanions { get; set; }
}

/// <summary>To a companion: it joined, in <see cref="Slot"/>.</summary>
[MessagePackObject]
public class PairingJoinedPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Joined"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Joined;
    /// <summary>The companion's slot (1..).</summary>
    [Key(1)] public int Slot { get; set; }
}

/// <summary>
/// A peer came or went. To the host: a companion slot. To a companion: slot 0 left, the session closed.
/// </summary>
[MessagePackObject]
public class PairingPeerPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Peer"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Peer;
    /// <summary>The peer's slot (0 = host).</summary>
    [Key(1)] public int Slot { get; set; }
    /// <summary>True when it joined, false when it left.</summary>
    [Key(2)] public bool Present { get; set; }
}

/// <summary>A payload relayed from a peer.</summary>
[MessagePackObject]
public class PairingMessagePacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Message"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Message;
    /// <summary>The sender's slot (0 = host).</summary>
    [Key(1)] public int From { get; set; }
    /// <summary>The bytes it sent.</summary>
    [Key(2)] public byte[] Payload { get; set; } = Array.Empty<byte>();
}

/// <summary>A pairing request was refused.</summary>
[MessagePackObject]
public class PairingRejectedPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Rejected"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Rejected;
    /// <summary>A <see cref="PairingRejectReason"/> value.</summary>
    [Key(1)] public int Reason { get; set; }
}

/// <summary>
/// Keep-alive (gate <c>pair-ping</c>, no reply): a pairing connection whose traffic moved to a
/// peer-to-peer link sends nothing else, and the transport closes idle connections.
/// </summary>
[MessagePackObject]
public class PairingPingPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="PairingPacketCodes.Ping"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = PairingPacketCodes.Ping;
}
