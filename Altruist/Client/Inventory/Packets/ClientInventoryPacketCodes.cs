namespace Altruist.Client.Inventory.Packets;

/// <summary>
/// Wire MessageCode constants for the client-inventory module.
/// Reserved range: <c>3000..3099</c>.
/// </summary>
public static class ClientInventoryPacketCodes
{
    /// <summary>Code of <see cref="ItemSnapshotPacket"/>.</summary>
    public const uint ItemSnapshot = 3000;
    /// <summary>Code of <see cref="ItemSlotClearedPacket"/>.</summary>
    public const uint ItemSlotCleared = 3001;
}
