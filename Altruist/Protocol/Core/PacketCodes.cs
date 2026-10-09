namespace Altruist;

/// <summary>
/// Reserved <see cref="IPacketBase.MessageCode"/> values for built-in framework packets.
/// User-defined packets are recommended to start from 1000+.
/// </summary>
/// <remarks>
/// Code <c>0</c> is reserved as "invalid / unset": the client dispatcher drops frames with
/// code 0 and refuses to register packet types whose default instance reports 0. Optional
/// modules reserve their own ranges (for example the client inventory module uses
/// 3000..3099).
/// </remarks>
public static class PacketCodes
{
    // 0 reserved / invalid
    /// <summary>Plain text message packet (server <c>TextPacket</c>).</summary>
    public const uint Text = 1;
    /// <summary>Message passed between server processes (server <c>InterprocessPacket</c>).</summary>
    public const uint Interprocess = 2;
    /// <summary>State synchronization packet (server <c>SyncPacket</c>).</summary>
    public const uint Sync = 3;
    /// <summary>Generic framework packet (server <c>AltruistPacket</c>).</summary>
    public const uint Altruist = 4;

    /// <summary>Positive result / acknowledgement (server <c>SuccessPacket</c>).</summary>
    public const uint Success = 5;
    /// <summary>Negative result carrying a failure reason (server <c>FailedPacket</c>).</summary>
    public const uint Failed = 6;

    /// <summary>Client handshake request (server <c>HandshakeRequestPacket</c>).</summary>
    public const uint HandshakeRequest = 7;
    /// <summary>Server handshake reply (server <c>HandshakeResponsePacket</c>).</summary>
    public const uint HandshakeResponse = 8;

    /// <summary>Request to join a game / room (server <c>JoinGamePacket</c>).</summary>
    public const uint JoinGame = 9;
    /// <summary>Request to leave a game / room (server <c>LeaveGamePacket</c>).</summary>
    public const uint LeaveGame = 10;

    /// <summary>Room state / membership packet (server <c>RoomPacket</c>).</summary>
    public const uint Room = 11;
    /// <summary>Session authentication packet (server security module).</summary>
    public const uint SessionAuth = 12;
    /// <summary>World-object state snapshot for the server dashboard (server <c>DashboardWorldObjectStatePacket</c>).</summary>
    public const uint DashboardWorldObjectState = 13;
}
