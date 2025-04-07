using Altruist;
using MessagePack;

namespace Altruist.Gaming;

public class InventoryItem
{
    public long Id { get; set; }
    public short Count { get; set; }
    public int[] Properties { get; set; }

    public string Category { get; set; }

    public DateTime? ExpiryDate { get; set; } // Optional: For consumables
    public bool Stackable { get; set; } = false;

    public byte Width { get; set; }
    public byte Height { get; set; }

    public InventoryItem(int itemPropertySize = 4, byte width = 1, byte height = 1, string itemType = default!, bool isStackable = false, DateTime? expiryDate = null)
    {
        Properties = new int[itemPropertySize];
        Category = itemType;
        Stackable = isStackable;
        ExpiryDate = expiryDate;
        Width = width;
        Height = height;
    }
}


[MessagePackObject]
public struct SlotKey
{
    [Key(0)] public string Type = "SlotKey";
    [Key(1)] public short X;
    [Key(2)] public short Y;
    [Key(3)] public string Id;
    [Key(4)] public string StorageId;

    public SlotKey(short x, short y, string id = "inventory", string storageId = "inventory")
    {
        Id = id;
        X = x;
        Y = y;
        StorageId = storageId;
    }

    public string ToKey()
    {
        return $"slot:{StorageId}:{Id}:{X}:{Y}";
    }
}

public class StorageSlot
{
    public SlotKey SlotKey { get; set; }
    public long ItemId { get; set; }
    public short ItemCount { get; set; }

    public short MaxCapacity { get; set; } = 1;
}