namespace Altruist.Gaming;

using MessagePack;
using System.Text.Json.Serialization;

[MessagePackObject]
public readonly struct SlotKey : IEquatable<SlotKey>
{
    [Key(0)][JsonPropertyName("type")] public string Type { get; }
    [Key(1)][JsonPropertyName("x")] public short X { get; }
    [Key(2)][JsonPropertyName("y")] public short Y { get; }
    [Key(3)][JsonPropertyName("id")] public string Id { get; }
    [Key(4)][JsonPropertyName("storageId")] public string StorageId { get; }

    public SlotKey(short x, short y, string id = "inventory", string storageId = "inventory")
    {
        Type = "SlotKey";
        X = x;
        Y = y;
        Id = id;
        StorageId = storageId;
    }

    public string ToKey()
    {
        return $"slot:{StorageId}:{Id}:{X}:{Y}";
    }

    public bool Equals(SlotKey other)
    {
        return X == other.X &&
               Y == other.Y &&
               Id == other.Id &&
               StorageId == other.StorageId;
    }

    public override bool Equals(object? obj)
    {
        return obj is SlotKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Combine hash codes efficiently
        return HashCode.Combine(X, Y, Id, StorageId);
    }

    public static bool operator ==(SlotKey left, SlotKey right) => left.Equals(right);
    public static bool operator !=(SlotKey left, SlotKey right) => !(left == right);
}


public static class SlotKeys
{
    public static SlotKey InventoryAnyPos = new SlotKey(-1, -1);
    public static SlotKey GroundAnyPos = new SlotKey(-1, -1, "ground", "ground");
}


[MessagePackObject]
public struct ItemDropPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    [JsonPropertyName("itemId")]
    [Key(1)]
    public string ItemId { get; set; }

    [JsonPropertyName("properties")]
    [Key(2)]
    public int[] Properties { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")]
    [Key(3)]
    public string Type { get; set; } = "ItemDropPacket";

    public ItemDropPacket()
    {
        Properties = Array.Empty<int>();
        ItemId = "";
    }

    public ItemDropPacket(string sender, string itemId, int[] properties, string? receiver = null)
    {
        Header = new PacketHeader(sender, receiver);
        ItemId = itemId;
        Properties = properties;
    }
}

/// <summary>
/// Removes an item from the specified storage location, either via grid coordinates or slot ID.
/// </summary>
[MessagePackObject]
public struct ItemRemovePacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target slot identifier, used for fixed-slot storages like "helmet".</summary>
    [JsonPropertyName("slotKey")]
    [Key(1)]
    public SlotKey SlotKey { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")]
    [Key(2)]
    public string Type { get; set; } = "InventoryRemoveItemPacket";

    public ItemRemovePacket()
    {

    }

    public ItemRemovePacket(string sender, SlotKey slotKey, string? receiver = null)
    {
        SlotKey = slotKey;
        Header = new PacketHeader(sender, receiver);
    }
}


[MessagePackObject]
public struct ItemPickUpPacket : IPacketBase
{
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    [JsonPropertyName("itemId")]
    [Key(1)]
    public string ItemId { get; set; }

    [JsonPropertyName("itemCount")]
    [Key(2)]
    public short ItemCount { get; set; }

    [JsonPropertyName("storageId")]
    [Key(3)]
    public string? TargetStorageId { get; set; } = "inventory";

    [JsonPropertyName("slotId")]
    [Key(4)]
    public string? TargetSlotId { get; set; } = "inventory";

    [JsonPropertyName("type")]
    [Key(5)]
    public string Type { get; set; } = "ItemPickUpPacket";

    public ItemPickUpPacket()
    {
        ItemId = "";
    }

    public ItemPickUpPacket(string sender, string itemId, short itemCount, string? targetStorageId = "inventory", string? targetSlotId = "inventory", string? receiver = null)
    {
        ItemId = itemId;
        ItemCount = itemCount;
        TargetSlotId = targetSlotId;
        TargetStorageId = targetStorageId;
        Header = new PacketHeader(sender, receiver);
    }
}


/// <summary>
/// Sets (adds or replaces) an item into a storage location such as inventory, equipment, or any other defined storage.
/// Can place items using grid coordinates (x, y) or specific slotId for fixed-slot storage.
/// </summary>
[MessagePackObject]
public struct ItemSetPacket : IPacketBase
{
    /// <summary>Packet routing header.</summary>
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target storage ID (e.g., "inventory", "equipment").</summary>
    [JsonPropertyName("storageId")]
    [Key(1)]
    public string StorageId { get; set; }

    /// <summary>ID of the item to set.</summary>
    [JsonPropertyName("itemId")]
    [Key(2)]
    public string ItemId { get; set; }

    /// <summary>Count of the item.</summary>
    [JsonPropertyName("itemCount")]
    [Key(3)]
    public short ItemCount { get; set; }

    /// <summary>Target slot identifier for fixed-slot storages like equipment (e.g., "helmet", "amulet").</summary>
    [JsonPropertyName("slotKey")]
    [Key(4)]
    public SlotKey SlotKey { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")]
    [Key(5)]
    public string Type { get; set; } = "InventorySetItemPacket";

    public ItemSetPacket()
    {
        StorageId = "inventory";
        ItemId = "";
    }

    public ItemSetPacket(string sender, string storageId, string itemId, SlotKey slotKey, short itemCount = 1, string? receiver = null)
    {
        SlotKey = slotKey;
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
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>ID of the item to move. The server uses this to locate the original position.</summary>
    [JsonPropertyName("itemId")]
    [Key(1)]
    public string ItemId { get; set; }

    /// <summary>Amount of the item to move (if stackable).</summary>
    [JsonPropertyName("itemCount")]
    [Key(2)]
    public short ItemCount { get; set; }

    /// <summary>Source slot ID (used for fixed slot destinations like "ring", "helmet").</summary>
    [JsonPropertyName("slotKey")]
    [Key(3)]
    public SlotKey SlotKey { get; set; }

    /// <summary>Target slot ID (used for fixed slot destinations like "ring", "helmet").</summary>
    [JsonPropertyName("targetSlotKey")]
    [Key(4)]
    public SlotKey TargetSlotKey { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")]
    [Key(5)]
    public string Type { get; set; } = "InventoryMoveItemPacket";

    public InventoryMoveItemPacket()
    {
        ItemId = "";
    }

    public InventoryMoveItemPacket(string sender, SlotKey fromSlot, SlotKey toSlot, string itemId, short itemCount = 1, string? receiver = null)
    {
        SlotKey = fromSlot;
        TargetSlotKey = toSlot;
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
    [JsonPropertyName("header")]
    [Key(0)]
    public PacketHeader Header { get; set; }

    /// <summary>Target storage to sort (e.g., "inventory", "bank").</summary>
    [JsonPropertyName("storageId")]
    [Key(1)]
    public string StorageId { get; set; }

    /// <summary>Type identifier of the packet.</summary>
    [JsonPropertyName("type")]
    [Key(2)]
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
