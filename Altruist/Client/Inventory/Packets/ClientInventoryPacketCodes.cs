namespace Altruist.Client.Inventory.Packets;

/// <summary>
/// Wire MessageCode constants for the client-inventory module.
/// Reserved range: <c>3000..3099</c>.
/// </summary>
public static class ClientInventoryPacketCodes
{
    public const uint ItemSnapshot = 3000;
    public const uint ItemSlotCleared = 3001;
}
