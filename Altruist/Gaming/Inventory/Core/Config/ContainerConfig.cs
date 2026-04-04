/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Configuration for a single inventory container.
/// </summary>
public class ContainerConfig
{
    /// <summary>Container identifier (e.g. "inventory", "bank", "belt").</summary>
    public string ContainerId { get; set; } = "inventory";

    /// <summary>Container type: Grid, Slot, or Equipment.</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.Slot;

    /// <summary>Width for grid containers.</summary>
    public short Width { get; set; } = 10;

    /// <summary>Height for grid containers.</summary>
    public short Height { get; set; } = 6;

    /// <summary>Number of slots for slot-based containers.</summary>
    public short SlotCount { get; set; } = 45;

    /// <summary>Maximum stack size per slot.</summary>
    public short SlotCapacity { get; set; } = 99;

    /// <summary>Equipment slot definitions (only for Equipment container type).</summary>
    public List<EquipmentSlotDefinition>? EquipmentSlots { get; set; }
}

/// <summary>
/// Defines a single equipment slot with accepted item categories.
/// </summary>
public class EquipmentSlotDefinition
{
    /// <summary>Slot name (e.g. "head", "chest", "weapon_main").</summary>
    public string SlotName { get; set; } = "";

    /// <summary>Slot index encoded as X in SlotKey.</summary>
    public short SlotIndex { get; set; }

    /// <summary>Item categories accepted by this slot. Empty = accept all.</summary>
    public string[] AcceptedCategories { get; set; } = [];
}
