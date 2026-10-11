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
/// <remarks>
/// An item is a runtime instance; its static definition is an <see cref="ItemTemplate"/>, whose
/// <see cref="ItemTemplate.CreateInstance"/> produces it (normally via <see cref="IInventoryService.CreateItem"/>).
/// Containers store only <see cref="InstanceId"/>, <see cref="TemplateId"/> and the slot's count; the service keeps the
/// object. The equip/use callbacks are invoked by <see cref="AltruistInventoryPortal"/> (or your own code), never by the
/// service or containers.
/// </remarks>
/// <example>
/// <code>
/// public class Sword : GameItem
/// {
///     public int Damage { get; set; }
///     public override string? EquipmentSlotType =&gt; "weapon_main";
///     public override void OnEquip(PlayerEntity player) { /* add damage */ }
///     public override void OnUnequip(PlayerEntity player) { /* remove damage */ }
/// }
/// </code>
/// </example>
public abstract class GameItem
{
    /// <summary>Unique instance ID for this item (generated on creation).</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Links to the <see cref="ItemTemplate.ItemId"/> this item was created from; same-template stackable items stack together.</summary>
    public long TemplateId { get; set; }

    /// <summary>Category label (e.g. "weapon", "armor", "consumable"); matched against <see cref="EquipmentSlotDefinition.AcceptedCategories"/>.</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Stack count. For non-stackable items, always 1. Used as the amount placed by <see cref="IInventoryService.AddItem"/>;
    /// afterwards the slot's <see cref="StorageSlot.ItemCount"/> is authoritative (only <see cref="IInventoryService.RemoveItem"/> re-syncs this value).
    /// </summary>
    public short Count { get; set; } = 1;

    /// <summary>Whether this item can be stacked with others of the same template.</summary>
    public bool Stackable { get; set; }

    /// <summary>Maximum stack size per slot. Informational: the built-in containers cap stacks with the slot's <see cref="StorageSlot.MaxCapacity"/> instead.</summary>
    public short MaxStack { get; set; } = 1;

    /// <summary>Grid dimensions in cells for <see cref="GridStorage"/> (X = width, Y = height). Default 1x1. Ignored by other containers.</summary>
    public ByteVector2 Size { get; set; } = new(1, 1);

    /// <summary>Optional expiration date in UTC. Null means no expiry. Checked by <see cref="IInventoryService.UseItemAsync"/>.</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Timestamp (UTC) when this item instance was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last known slot, set by <see cref="IInventoryService.AddItem"/> to the requested key (an auto key when placed
    /// automatically). Not updated by moves, swaps or the containers; use <see cref="IInventoryContainer.FindItemSlot"/> for the real position.
    /// </summary>
    public SlotKey CurrentSlot { get; set; }

    /// <summary>Whether this item has expired (<see cref="ExpiryDate"/> is before <see cref="DateTime.UtcNow"/>).</summary>
    public bool IsExpired => ExpiryDate.HasValue && DateTime.UtcNow > ExpiryDate.Value;

    /// <summary>
    /// The equipment slot type this item can be equipped to (e.g. "head", "weapon_main").
    /// Return null if the item is not equippable.
    /// </summary>
    public virtual string? EquipmentSlotType => null;

    /// <summary>
    /// Check if this item can be equipped to a specific named slot. Default: exact (case-sensitive) match with
    /// <see cref="EquipmentSlotType"/>. Override for items that fit several slots (e.g. rings in "ring_1"/"ring_2").
    /// </summary>
    /// <param name="slotName"><see cref="EquipmentSlotDefinition.SlotName"/> being tested.</param>
    /// <returns>True when the item may go in that slot.</returns>
    public virtual bool CanEquipTo(string slotName) => slotName == EquipmentSlotType;

    /// <summary>Called by <see cref="AltruistInventoryPortal"/> after a successful equip. Override to apply stat bonuses.</summary>
    /// <param name="player">The equipping player.</param>
    public virtual void OnEquip(PlayerEntity player) { }

    /// <summary>Called by <see cref="AltruistInventoryPortal"/> after a successful unequip. Override to remove stat bonuses.</summary>
    /// <param name="player">The unequipping player.</param>
    public virtual void OnUnequip(PlayerEntity player) { }

    /// <summary>Called by <see cref="AltruistInventoryPortal"/> after a successful use. Override for consumable effects; consumption itself is up to the caller.</summary>
    /// <param name="player">The using player.</param>
    public virtual void OnUse(PlayerEntity player) { }
}
