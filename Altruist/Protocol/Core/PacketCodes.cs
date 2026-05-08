namespace Altruist;

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
    public const uint SessionAuth = 12;
    public const uint DashboardWorldObjectState = 13;
}
