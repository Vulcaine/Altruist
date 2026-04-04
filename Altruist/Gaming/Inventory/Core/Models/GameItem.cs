/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Base class for all game items. Users extend this to define their own item types
/// with custom properties, equipment behavior, and use effects.
/// Apply [MessagePackObject] to your concrete subclasses for serialization.
/// </summary>
public abstract class GameItem
{
    /// <summary>Unique instance ID for this item (generated on creation).</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Links to the ItemTemplate this item was created from.</summary>
    public long TemplateId { get; set; }

    /// <summary>Category label (e.g. "weapon", "armor", "consumable").</summary>
    public string Category { get; set; } = "";

    /// <summary>Stack count. For non-stackable items, always 1.</summary>
    public short Count { get; set; } = 1;

    /// <summary>Whether this item can be stacked with others of the same template.</summary>
    public bool Stackable { get; set; }

    /// <summary>Maximum stack size per slot.</summary>
    public short MaxStack { get; set; } = 1;

    /// <summary>Grid dimensions for grid-based containers (width x height). Default 1x1.</summary>
    public ByteVector2 Size { get; set; } = new(1, 1);

    /// <summary>Optional expiration date. Null means no expiry.</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Timestamp when this item instance was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Current slot position. Updated by the container on placement.</summary>
    public SlotKey CurrentSlot { get; set; }

    /// <summary>Whether this item has expired.</summary>
    public bool IsExpired => ExpiryDate.HasValue && DateTime.UtcNow > ExpiryDate.Value;

    /// <summary>
    /// The equipment slot type this item can be equipped to (e.g. "head", "weapon_main").
    /// Return null if the item is not equippable.
    /// </summary>
    public virtual string? EquipmentSlotType => null;

    /// <summary>Check if this item can be equipped to a specific named slot.</summary>
    public virtual bool CanEquipTo(string slotName) => slotName == EquipmentSlotType;

    /// <summary>Called when the item is equipped. Override to apply stat bonuses.</summary>
    public virtual void OnEquip(PlayerEntity player) { }

    /// <summary>Called when the item is unequipped. Override to remove stat bonuses.</summary>
    public virtual void OnUnequip(PlayerEntity player) { }

    /// <summary>Called when the item is used. Override for consumable effects.</summary>
    public virtual void OnUse(PlayerEntity player) { }
}
