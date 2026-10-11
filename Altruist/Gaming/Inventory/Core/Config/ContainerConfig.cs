/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Configuration for a single inventory container, passed to <see cref="IInventoryService.CreateContainer"/>.
/// Only the fields relevant to <see cref="ContainerType"/> are read: Grid uses Width/Height/SlotCapacity,
/// Slot uses SlotCount/SlotCapacity, Equipment uses EquipmentSlots. Plain POCO (not bound from appsettings by the framework).
/// </summary>
/// <example>
/// <code>
/// inventory.CreateContainer(playerId, new ContainerConfig { ContainerId = "inventory", ContainerType = ContainerType.Grid, Width = 10, Height = 6 });
/// inventory.CreateContainer(playerId, new ContainerConfig { ContainerId = "bank", ContainerType = ContainerType.Slot, SlotCount = 80 });
/// inventory.CreateContainer(playerId, new ContainerConfig
/// {
///     ContainerId = "equipment",
///     ContainerType = ContainerType.Equipment,
///     EquipmentSlots = [ new() { SlotName = "head", SlotIndex = 0 }, new() { SlotName = "weapon_main", SlotIndex = 1, AcceptedCategories = ["weapon"] } ]
/// });
/// </code>
/// </example>
public class ContainerConfig
{
    /// <summary>
    /// Container identifier (e.g. "inventory", "bank", "belt"); together with the owner id it forms the container's address.
    /// The service's convenience operations assume the names <c>"inventory"</c> and <c>"equipment"</c>;
    /// for Equipment containers use <c>"equipment"</c>, since <see cref="EquipmentStorage"/> always reports that id.
    /// </summary>
    public string ContainerId { get; set; } = "inventory";

    /// <summary>Container type: Grid, Slot, or Equipment (default Slot).</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.Slot;

    /// <summary>Width in cells for grid containers (default 10).</summary>
    public short Width { get; set; } = 10;

    /// <summary>Height in cells for grid containers (default 6).</summary>
    public short Height { get; set; } = 6;

    /// <summary>Number of slots for slot-based containers (default 45).</summary>
    public short SlotCount { get; set; } = 45;

    /// <summary>Maximum stack size per slot for Grid and Slot containers (default 99). Equipment slots always hold 1. <see cref="GameItem.MaxStack"/> is not consulted.</summary>
    public short SlotCapacity { get; set; } = 99;

    /// <summary>Equipment slot definitions (only for Equipment container type; null = no slots).</summary>
    public List<EquipmentSlotDefinition>? EquipmentSlots { get; set; }
}

/// <summary>
/// Defines a single equipment slot with accepted item categories. An item fits when
/// <see cref="GameItem.CanEquipTo"/> returns true for <see cref="SlotName"/> and, if <see cref="AcceptedCategories"/>
/// is non-empty, its <see cref="GameItem.Category"/> is listed.
/// </summary>
public class EquipmentSlotDefinition
{
    /// <summary>Slot name (e.g. "head", "chest", "weapon_main"); looked up case-insensitively, must be unique within the container.</summary>
    public string SlotName { get; set; } = "";

    /// <summary>Slot index encoded as <see cref="SlotKey.X"/> (Y is always 0); must be unique within the container.</summary>
    public short SlotIndex { get; set; }

    /// <summary>Item categories accepted by this slot (case-insensitive). Empty = accept all.</summary>
    public string[] AcceptedCategories { get; set; } = [];
}
