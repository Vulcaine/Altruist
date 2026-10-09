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
/// <remarks>
/// A template is the static definition shared by all instances; register it with <see cref="IItemTemplateProvider"/>
/// and create instances via <see cref="IInventoryService.CreateItem"/> (which also tracks the instance).
/// </remarks>
/// <example>
/// <code>
/// public class MyItemTemplate : ItemTemplate
/// {
///     public int Damage { get; set; }
///     public override GameItem CreateInstance(short count = 1) =&gt; new MyItem
///     {
///         Category = Category, Stackable = Stackable, MaxStack = MaxStack, Size = Size, Damage = Damage
///     };
/// }
/// </code>
/// </example>
public abstract class ItemTemplate
{
    /// <summary>Unique numeric ID for this template; becomes <see cref="GameItem.TemplateId"/>.</summary>
    public long ItemId { get; set; }

    /// <summary>String key for this template (e.g. "iron_sword"); optional, used by <see cref="IItemTemplateProvider.GetTemplateByKey"/>.</summary>
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

    /// <summary>Equipment slot type (null = not equippable). Not applied automatically: <see cref="GameItem.EquipmentSlotType"/> is a virtual property your item type must override.</summary>
    public string? EquipmentSlotType { get; set; }

    /// <summary>
    /// Create a GameItem instance from this template.
    /// Users must override this in their template subclass and copy the template fields they need
    /// (Category, Stackable, MaxStack, Size, ...); <see cref="IInventoryService.CreateItem"/> only sets TemplateId and Count afterwards.
    /// </summary>
    /// <param name="count">Requested stack count.</param>
    /// <returns>A new, untracked item instance.</returns>
    public abstract GameItem CreateInstance(short count = 1);
}
