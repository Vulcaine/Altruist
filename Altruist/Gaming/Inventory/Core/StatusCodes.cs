/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Result code of every inventory operation (containers, <see cref="IInventoryService"/> and the portal).
/// Sent to clients as its integer value in <see cref="ItemResultPacket.Status"/>, so the numeric values are wire-stable.
/// </summary>
public enum ItemStatus
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,
    /// <summary>Destination is occupied, out of bounds for the item's footprint, or the container is full.</summary>
    NotEnoughSpace = 1,
    /// <summary>No item at the given slot / instance id, or the item is not tracked by the service.</summary>
    ItemNotFound = 2,
    /// <summary>No container registered for the given owner + container id.</summary>
    StorageNotFound = 3,
    /// <summary>Slot position does not exist in the container (or an unknown equipment slot name).</summary>
    InvalidSlot = 4,
    /// <summary>Reserved: item cannot be stacked. Built-in containers currently report <see cref="NotEnoughSpace"/> instead.</summary>
    NonStackable = 5,
    /// <summary>Same-template stack exists at the target but would exceed the slot capacity (<see cref="SlotStorage"/>).</summary>
    StackFull = 6,
    /// <summary>Reserved for invalid counts; not returned by the built-in implementation.</summary>
    BadCount = 7,
    /// <summary>Reserved for game-specific move denials (e.g. from a portal hook); not returned by the built-in implementation.</summary>
    CannotMove = 8,
    /// <summary>Item cannot go into that equipment slot (not equippable, wrong slot type or category).</summary>
    IncompatibleSlot = 9,
    /// <summary>Item's <see cref="GameItem.ExpiryDate"/> has passed (returned by <see cref="IInventoryService.UseItemAsync"/>).</summary>
    ItemExpired = 10,
    /// <summary>Destination container rejected the item in <see cref="IInventoryContainer.ValidateItem"/>.</summary>
    ValidationFailed = 11
}

/// <summary>
/// Layout of a container, selected via <see cref="ContainerConfig.ContainerType"/> when calling
/// <see cref="IInventoryService.CreateContainer"/>.
/// </summary>
public enum ContainerType
{
    /// <summary>2D grid where items occupy <see cref="GameItem.Size"/> cells (Diablo-style); creates <see cref="GridStorage"/>.</summary>
    Grid,
    /// <summary>Flat list of 1x1 stack slots (WoW-style bags, banks, belts); creates <see cref="SlotStorage"/>. Also reported by <see cref="WorldItemStorage"/>.</summary>
    Slot,
    /// <summary>Named single-item slots with category rules; creates <see cref="EquipmentStorage"/>.</summary>
    Equipment
}

/// <summary>
/// Result of a move-style operation (move, swap, pickup, drop, equip, unequip, remove) on <see cref="IInventoryService"/>.
/// </summary>
/// <param name="Status">Outcome code.</param>
/// <param name="Item">The item that was moved/removed on success (for swaps, the item that left slot A); usually null on failure.</param>
public record MoveItemResult(ItemStatus Status, GameItem? Item = null);
/// <summary>
/// Result of <see cref="IInventoryService.UseItemAsync"/>. A success only means the item exists and is not expired;
/// the effect (<see cref="GameItem.OnUse"/>) and any consumption are applied by the caller.
/// </summary>
/// <param name="Status">Outcome code.</param>
/// <param name="Item">The item to use on success; null on failure.</param>
public record UseItemResult(ItemStatus Status, GameItem? Item = null);
