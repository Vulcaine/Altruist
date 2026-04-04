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
public class StorageSlot
{
    /// <summary>Position of this slot within its container.</summary>
    public SlotKey SlotKey { get; set; }

    /// <summary>Instance ID of the item in this slot. Empty = slot is empty.</summary>
    public string ItemInstanceId { get; set; } = "";

    /// <summary>Template ID of the item (for quick lookup without fetching the full item).</summary>
    public long ItemTemplateId { get; set; }

    /// <summary>Number of items in this slot.</summary>
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

    public void Clear()
    {
        ItemInstanceId = "";
        ItemTemplateId = 0;
        ItemCount = 0;
        SlotLink = null;
    }

    public void Set(string instanceId, long templateId, short count)
    {
        ItemInstanceId = instanceId;
        ItemTemplateId = templateId;
        ItemCount = count;
    }
}
