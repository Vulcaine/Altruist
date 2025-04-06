
public class InventoryStorage
{
    public string StorageId { get; set; }
    public Dictionary<string, StorageSlot> SlotMap { get; set; } = new();

    public InventoryStorage(string storageId)
    {
        StorageId = storageId;
    }
}

public class StorageSlot
{
    public short X { get; set; }
    public short Y { get; set; }
    public string StorageId { get; set; } = "inventory";
    public string? SlotId { get; set; }
    public long ItemId { get; set; }
    public short ItemCount { get; set; }

    public string GetKey() {
        return $"slot:{StorageId}:{SlotId ?? ""}:{X}:{Y}";
    }
}

public class InventoryItem<T> where T : Enum
{
    public long ItemId { get; set; }
    public int Count { get; set; }
    public int[] Properties { get; set; }

    public T ItemType { get; set; }

    public DateTime? ExpiryDate { get; set; } // Optional: For consumables
    public bool IsStackable { get; set; } = false;

    public InventoryItem(int itemPropertySize = 4, T itemType = default!, bool isStackable = false, DateTime? expiryDate = null)
    {
        Properties = new int[itemPropertySize];
        ItemType = itemType;
        IsStackable = isStackable;
        ExpiryDate = expiryDate;
    }
}
