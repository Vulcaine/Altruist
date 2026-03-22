/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Immutable item definition/prototype. Shared by all instances of the same item type.
/// Users create templates at startup via IItemTemplateProvider.
/// </summary>
public class ItemTemplate
{
    /// <summary>Unique numeric ID for this template.</summary>
    public long ItemId { get; set; }

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
    /// Factory method to create an item instance from this template.
    /// Override in subclasses to create custom GameItem types.
    /// </summary>
    public virtual GameItem CreateInstance(short count = 1)
    {
        throw new InvalidOperationException(
            $"ItemTemplate '{Name}' (ID={ItemId}) does not override CreateInstance(). " +
            $"Provide a concrete GameItem subclass or override this method.");
    }
}
