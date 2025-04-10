using System.Text.Json.Serialization;

namespace Altruist.Gaming;

public abstract class BasicItemProperties
{
    /// <summary>
    /// Number of units of this item. Typically used for stackable items like potions.
    /// </summary>
    public short Count { get; set; }

    /// <summary>
    /// Array of item-specific dynamic properties such as durability, charges, or enchantments.
    /// Size is determined during item creation.
    /// </summary>
    public int[] Properties { get; set; }

    /// <summary>
    /// Category of the item, e.g., "weapon", "armor", "consumable".
    /// Used for filtering and sorting in UI and storage.
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// Optional expiration date. Primarily used for consumables or timed buffs.
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Indicates whether this item can be stacked with others of the same type.
    /// </summary>
    public bool Stackable { get; set; } = false;

    /// <summary>
    /// Size of the item in inventory slots.
    /// </summary>
    public ByteVector2 Size { get; set; }


    /// <summary>
    /// Constructs a new inventory item with customizable dimensions, category, and properties.
    /// </summary>
    /// <param name="itemPropertySize">Number of custom property slots.</param>
    /// <param name="width">Grid width of the item.</param>
    /// <param name="height">Grid height of the item.</param>
    /// <param name="itemType">Category or type label.</param>
    /// <param name="isStackable">True if the item is stackable.</param>
    /// <param name="expiryDate">Optional expiration date.</param>
    public BasicItemProperties(int itemPropertySize = 4, byte width = 1, byte height = 1, string itemType = default!, bool isStackable = false, DateTime? expiryDate = null)
    {
        Properties = new int[itemPropertySize];
        Category = itemType;
        Stackable = isStackable;
        ExpiryDate = expiryDate;
        Size = new ByteVector2(width, height);
    }
}

public abstract class ItemTemplate : BasicItemProperties, IVaultModel
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("baseItem")]
    public string Type { get; set; } = "BaseItem";

    public abstract Task<List<IVaultModel>> PreLoad();
}

/// <summary>
/// Represents an item instance in the inventory. Each item is based on a template
/// and contains dynamic properties such as count, dimensions, category, and expiry.
/// </summary>
public abstract class GameItem : BasicItemProperties, IVaultModel
{
    /// <summary>
    /// Unique identifier for this constructed item instance. 
    /// It is generated at runtime and is not the same as the template ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    /// <summary>
    /// Static ID that links this item to its template definition.
    /// Template data is immutable and shared between items of the same type.
    /// </summary>
    [JsonPropertyName("templateId")]
    public long TemplateId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("gameItem")]
    public string Type { get; set; } = "GameItem";

    [JsonPropertyName("slotKey")]
    public SlotKey SlotKey { get; }
    /// <summary>
    /// Constructs a new inventory item with customizable dimensions, category, and properties.
    /// </summary>
    /// <param name="itemPropertySize">Number of custom property slots.</param>
    /// <param name="width">Grid width of the item.</param>
    /// <param name="height">Grid height of the item.</param>
    /// <param name="itemType">Category or type label.</param>
    /// <param name="isStackable">True if the item is stackable.</param>
    /// <param name="expiryDate">Optional expiration date.</param>
    public GameItem(SlotKey slotKey, int itemPropertySize = 4, byte width = 1, byte height = 1, string itemType = default!, bool isStackable = false, DateTime? expiryDate = null)
    {
        Id = Guid.NewGuid().ToString();
        SlotKey = slotKey;
        Properties = new int[itemPropertySize];
        Category = itemType;
        Stackable = isStackable;
        ExpiryDate = expiryDate;
        Size = new ByteVector2(width, height);
    }

    public abstract Task<List<IVaultModel>> PreLoad();
}


/// <summary>
/// Represents a single grid slot in a storage. 
/// It holds metadata about the stored item, quantity, and max capacity.
/// </summary>
public class StorageSlot
{
    /// <summary>
    /// Unique identifier for the slot including position and storage info.
    /// </summary>
    [JsonPropertyName("slotKey")]
    public SlotKey SlotKey { get; set; }

    /// <summary>
    /// ID of the item currently occupying this slot.
    /// A value of "" typically means the slot is empty.
    /// </summary>
    [JsonPropertyName("itemInstanceId")]
    public string ItemInstanceId { get; set; } = "";

    /// <summary>
    /// The template ID of the item currently occupying this slot.
    /// A value of 0 typically means the slot is empty.
    /// </summary>
    [JsonPropertyName("itemTemplateId")]
    public long ItemTemplateId { get; set; }

    /// <summary>
    /// The number of items currently in this slot.
    /// For non-stackable items, this is typically 1.
    /// </summary>
    [JsonPropertyName("itemCount")]
    public short ItemCount { get; set; }

    /// <summary>
    /// The maximum number of items this slot can hold.
    /// Used for validating stacking logic.
    /// </summary>
    [JsonPropertyName("maxCapacity")]
    public short MaxCapacity { get; set; } = 1;

    [JsonPropertyName("slotLink")]
    public IntVector2? SlotLink { get; set; }

    public void ClearSlot()
    {
        ItemInstanceId = "";
        ItemTemplateId = 0;
        ItemCount = 0;
        SlotLink = null;
    }
}


public record SlotGroup(List<StorageSlot> Slots)
{
    public StorageSlot Anchor => Slots[0];

    public short Width =>
        (short)(Slots.Max(s => s.SlotKey.X) - Slots.Min(s => s.SlotKey.X) + 1);

    public short Height =>
        (short)(Slots.Max(s => s.SlotKey.Y) - Slots.Min(s => s.SlotKey.Y) + 1);
}
