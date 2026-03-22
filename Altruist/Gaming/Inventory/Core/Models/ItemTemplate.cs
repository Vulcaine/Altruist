/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Base item template. Users extend this with their own properties and override
/// CreateInstance() to produce their GameItem type.
///
/// Templates can be registered programmatically or loaded from JSON files
/// (deserialized directly into the user's subclass).
/// </summary>
public abstract class ItemTemplate
{
    /// <summary>Unique numeric ID for this template.</summary>
    public long ItemId { get; set; }

    /// <summary>String key for this template (e.g. "iron_sword").</summary>
    public string Key { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Category label (e.g. "weapon", "armor", "consumable").</summary>
    public string Category { get; set; } = "";

    /// <summary>Whether items of this type can be stacked.</summary>
    public bool Stackable { get; set; }

    /// <summary>Maximum stack size.</summary>
    public short MaxStack { get; set; } = 1;

    /// <summary>Grid size for grid-based containers.</summary>
    public ByteVector2 Size { get; set; } = new(1, 1);

    /// <summary>Equipment slot type (null = not equippable).</summary>
    public string? EquipmentSlotType { get; set; }

    /// <summary>
    /// Create a GameItem instance from this template.
    /// Users must override this in their template subclass.
    /// </summary>
    public abstract GameItem CreateInstance(short count = 1);
}
