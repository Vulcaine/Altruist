using System.Text.Json.Serialization;
using MessagePack;

namespace Altruist.Gaming;

/// <summary>
/// Removes an item from the specified storage location, either via grid coordinates or slot ID.
/// </summary>
[MessagePackObject]
public struct InventoryRemoveItemPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")][Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target storage ID (e.g., "inventory", "equipment").</summary>
    [JsonPropertyName("storageId")][Key(1)]
    public string StorageId { get; set; }

    /// <summary>Target slot identifier, used for fixed-slot storages like "helmet".</summary>
    [JsonPropertyName("slotId")][Key(6)]
    public string? SlotId { get; set; }

    /// <summary>X position of item in grid-based storage.</summary>
    [JsonPropertyName("x")][Key(2)]
    public int X { get; set; }

    /// <summary>Y position of item in grid-based storage.</summary>
    [JsonPropertyName("y")][Key(3)]
    public int Y { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")][Key(4)]
    public string Type { get; set; } = "InventoryRemoveItemPacket";

    public InventoryRemoveItemPacket()
    {
        StorageId = "inventory";
        SlotId = "inventory";
    }

    public InventoryRemoveItemPacket(string sender, string storagetId, string slotId, int x = 0, int y = 0, string? receiver = null)
    {
        X = x;
        Y = y;
        StorageId = storagetId;
        SlotId = slotId;
        Header = new PacketHeader(sender, receiver);
    }
}



/// <summary>
/// Sets (adds or replaces) an item into a storage location such as inventory, equipment, or any other defined storage.
/// Can place items using grid coordinates (x, y) or specific slotId for fixed-slot storage.
/// </summary>
[MessagePackObject]
public struct InventorySetItemPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")][Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target storage ID (e.g., "inventory", "equipment").</summary>
    [JsonPropertyName("storageId")][Key(1)]
    public string StorageId { get; set; }

    /// <summary>ID of the item to set.</summary>
    [JsonPropertyName("itemId")][Key(2)]
    public long ItemId { get; set; }

    /// <summary>Count of the item.</summary>
    [JsonPropertyName("itemCount")][Key(3)]
    public short ItemCount { get; set; }

    /// <summary>X position in a grid-based inventory. Optional.</summary>
    [JsonPropertyName("x")][Key(4)]
    public short? X { get; set; }

    /// <summary>Y position in a grid-based inventory. Optional.</summary>
    [JsonPropertyName("y")][Key(5)]
    public short? Y { get; set; }

    /// <summary>Target slot identifier for fixed-slot storages like equipment (e.g., "helmet", "amulet").</summary>
    [JsonPropertyName("slotId")][Key(6)]
    public string? SlotId { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")][Key(7)]
    public string Type { get; set; } = "InventorySetItemPacket";

    public InventorySetItemPacket()
    {
        StorageId = "inventory";
        SlotId = "inventory";
        ItemId = 0;
    }

    public InventorySetItemPacket(string sender, string storageId, long itemId, short itemCount = 1, short? x = null, short? y = null, string? receiver = null)
    {
        X = x;
        Y = y;
        StorageId = storageId;
        ItemId = itemId;
        ItemCount = itemCount;
        Header = new PacketHeader(sender, receiver);
    }
}

/// <summary>
/// Moves an existing item to a new position in the same or different storage.
/// The system finds the source by itemId, and the packet defines the target location.
/// </summary>
[MessagePackObject]
public struct InventoryMoveItemPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")][Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Source storage ID for the move (e.g., "inventory", "equipment").</summary>
    [JsonPropertyName("storageId")][Key(1)]
    public string StorageId { get; set; }

     /// <summary>Target storage ID for the move (e.g., "inventory", "equipment").</summary>
    [JsonPropertyName("targetDtorageId")][Key(2)]
    public string TargetStorageId { get; set; }

    /// <summary>ID of the item to move. The server uses this to locate the original position.</summary>
    [JsonPropertyName("itemId")][Key(3)]
    public long ItemId { get; set; }

    /// <summary>Amount of the item to move (if stackable).</summary>
    [JsonPropertyName("itemCount")][Key(4)]
    public short ItemCount { get; set; }

    /// <summary>Target X coordinate (for grid-style storage).</summary>
    [JsonPropertyName("x")][Key(5)]
    public short X { get; set; }

    /// <summary>Target Y coordinate (for grid-style storage).</summary>
    [JsonPropertyName("y")][Key(6)]
    public short Y { get; set; }

    /// <summary>Source slot ID (used for fixed slot destinations like "ring", "helmet").</summary>
    [JsonPropertyName("slotId")][Key(7)]
    public string? SlotId { get; set; }

    /// <summary>Target slot ID (used for fixed slot destinations like "ring", "helmet").</summary>
    [JsonPropertyName("targetSlotId")][Key(8)]
    public string? TargetSlotId { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")][Key(8)]
    public string Type { get; set; } = "InventoryMoveItemPacket";

    public InventoryMoveItemPacket()
    {
        StorageId = "inventory";
        TargetStorageId = "inventory";
        SlotId = "inventory";
        ItemId = 0;
    }

    public InventoryMoveItemPacket(string sender, string storageId, string targetStorageId, string slotId, string targetSlotId, long itemId, short itemCount = 1, short x = 0, short y = 0, string? receiver = null)
    {
        X = x;
        Y = y;
        StorageId = storageId;
        TargetStorageId = targetStorageId;
        SlotId = slotId;
        TargetSlotId = targetSlotId;
        ItemId = itemId;
        ItemCount = itemCount;
        Header = new PacketHeader(sender, receiver);
    }
}


/// <summary>
/// Triggers automatic sorting of the specified storage.
/// Server will decide sorting order (e.g., by item type, rarity, ID).
/// </summary>
[MessagePackObject]
public struct InventorySortPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")][Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target storage to sort (e.g., "inventory", "bank").</summary>
    [JsonPropertyName("storageId")][Key(1)]
    public string StorageId { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")][Key(2)]
    public string Type { get; set; } = "InventorySortPacket";

    public InventorySortPacket()
    {
        StorageId = "inventory";
    }

    public InventorySortPacket(string sender, string storageId, string? receiver = null)
    {
        StorageId = storageId;
        Header = new PacketHeader(sender, receiver);
    }
}

/// <summary>
/// Packet sent to request using an item from a specific storage (e.g., inventory, bank).
/// </summary>
[MessagePackObject]
public struct UseItemPacket : IPacketBase
{
    [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

    /// <summary>
    /// Storage the item is located in, e.g., "inventory", "bank", "equipment".
    /// </summary>
    [JsonPropertyName("storageId")][Key(1)] public string StorageId { get; set; }

    /// <summary>
    /// Unique item identifier to be used.
    /// </summary>
    [JsonPropertyName("itemId")][Key(2)] public long ItemId { get; set; }

    [JsonPropertyName("type")][Key(3)] public string Type { get; set; } = "UseItemPacket";

    public UseItemPacket(string sender, string storageId, long itemId, string? receiver = null)
    {
        StorageId = storageId;
        ItemId = itemId;
        Header = new PacketHeader(sender, receiver);
    }
}

/// <summary>
/// Packet sent to request current state/snapshot of a specific storage (e.g., inventory, equipment).
/// </summary>
[MessagePackObject]
public struct StorageInfoPacket : IPacketBase
{
    [JsonPropertyName("header")][Key(0)] public PacketHeader Header { get; set; }

    /// <summary>
    /// The identifier of the storage (e.g., "inventory", "equipment", "bank").
    /// </summary>
    [JsonPropertyName("storageId")][Key(1)] public string StorageId { get; set; }

    [JsonPropertyName("type")][Key(2)] public string Type { get; set; } = "StorageInfoPacket";

    public StorageInfoPacket(string sender, string storageId, string? receiver = null)
    {
        StorageId = storageId;
        Header = new PacketHeader(sender, receiver);
    }
}
