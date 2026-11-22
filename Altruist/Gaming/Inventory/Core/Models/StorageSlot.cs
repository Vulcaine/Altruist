/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// A single slot in a container. Holds a reference to the item occupying it.
/// For grid-based containers, multi-cell items use SlotLink to chain occupied cells.
/// </summary>
/// <remarks>
/// Stores ids and counts only; resolve the item with <see cref="IInventoryService.GetItem"/>. The slot's
/// <see cref="ItemCount"/> is the authoritative stack size. Slots are owned and mutated by their container: read them,
/// but change contents through <see cref="IInventoryService"/> (or the container) rather than <see cref="Set"/>/<see cref="Clear"/>.
/// </remarks>
public class StorageSlot
{
    /// <summary>Full address of this slot (owner, container, position).</summary>
    public SlotKey SlotKey { get; set; }

    /// <summary>Instance ID of the item in this slot. Empty = slot is empty.</summary>
    public string ItemInstanceId { get; set; } = "";

    /// <summary>Template ID of the item (for quick lookup without fetching the full item).</summary>
    public long ItemTemplateId { get; set; }

    /// <summary>Number of items in this slot (0 on linked grid cells; the anchor holds the count).</summary>
    public short ItemCount { get; set; }

    /// <summary>Maximum stack capacity for this slot.</summary>
    public short MaxCapacity { get; set; } = 1;

    /// <summary>
    /// For multi-cell grid items: points to the anchor slot (top-left) that owns this cell.
    /// Null for single-cell items or the anchor cell itself.
    /// </summary>
    public IntVector2? SlotLink { get; set; }

    /// <summary>Whether this slot is empty (no item).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(ItemInstanceId) && ItemCount == 0;

    /// <summary>Whether this slot is a linked cell (part of a multi-cell item, not the anchor).</summary>
    public bool IsLinked => SlotLink.HasValue;

    /// <summary>Empties the slot (id, template, count and link). Low-level; containers call it on removal.</summary>
    public void Clear()
    {
        ItemInstanceId = "";
        ItemTemplateId = 0;
        ItemCount = 0;
        SlotLink = null;
    }

    /// <summary>Overwrites the slot's contents without any fit or stacking checks. Low-level; containers call it on placement.</summary>
    /// <param name="instanceId">Item instance id.</param>
    /// <param name="templateId">Item template id.</param>
    /// <param name="count">Stack count.</param>
    public void Set(string instanceId, long templateId, short count)
    {
        ItemInstanceId = instanceId;
        ItemTemplateId = templateId;
        ItemCount = count;
    }
}
