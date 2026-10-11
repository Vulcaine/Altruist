namespace Altruist;

/// <summary>Predefined <see cref="PacketHeader"/> values.</summary>
public static class PacketHeaders
{
    /// <summary>
    /// Header for server broadcasts: sender <c>"server"</c>, no receiver, timestamp 0.
    /// It is a struct, so each use gets its own copy; pass it to
    /// <see cref="MessageEnvelope(PacketHeader, IPacketBase)"/> and stamp the envelope if a
    /// timestamp is needed.
    /// </summary>
    public static readonly PacketHeader Broadcast = new PacketHeader
    {
        Sender = "server"
    };
}
