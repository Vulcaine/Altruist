/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Core container interface: a set of <see cref="StorageSlot"/>s that store item ids and stack counts (not the
/// <see cref="GameItem"/> objects themselves). Implemented by <see cref="GridStorage"/>, <see cref="SlotStorage"/>,
/// <see cref="EquipmentStorage"/>, and <see cref="WorldItemStorage"/>.
/// </summary>
/// <remarks>
/// Pick the implementation by layout: <see cref="GridStorage"/> for multi-cell items on a 2D grid,
/// <see cref="SlotStorage"/> for flat 1x1 stack slots (bags, banks, belts), <see cref="EquipmentStorage"/> for named
/// single-item slots, <see cref="WorldItemStorage"/> for unbounded ground items. Normally created and used through
/// <see cref="IInventoryService"/>, which also tracks the items and rolls back failed moves; calling the container
/// directly bypasses that bookkeeping. Implement it yourself for custom layouts and register it with the service
/// (the built-in <see cref="IInventoryService.CreateContainer"/> only builds the three <see cref="ContainerType"/>s).
/// Not thread-safe.
/// </remarks>
public interface IInventoryContainer
{
    /// <summary>Container id, the <see cref="SlotKey.ContainerId"/> of every slot.</summary>
    string ContainerId { get; }
    /// <summary>Owner id, the <see cref="SlotKey.OwnerId"/> of every slot.</summary>
    string OwnerId { get; }
    /// <summary>Layout kind.</summary>
    ContainerType ContainerType { get; }

    /// <summary>Get a specific slot by position.</summary>
    /// <param name="x">Column or flat index (see <see cref="SlotKey"/> conventions).</param>
    /// <param name="y">Row; 0 for non-grid containers.</param>
    /// <returns>The slot, or null when the position does not exist.</returns>
    StorageSlot? GetSlot(short x, short y);

    /// <summary>Get all slots in this container (a snapshot list; includes empty and linked grid cells).</summary>
    IReadOnlyCollection<StorageSlot> GetAllSlots();

    /// <summary>Get all non-empty slots (lazy; includes linked grid cells). Do not mutate the container while enumerating.</summary>
    IEnumerable<StorageSlot> GetOccupiedSlots();

    /// <summary>
    /// Try to place <paramref name="count"/> of <paramref name="item"/> at a specific position: into an empty slot, or
    /// stacked onto the same template when the item is stackable. Does not track the item or update
    /// <see cref="GameItem.CurrentSlot"/>.
    /// </summary>
    /// <param name="item">Item being placed.</param>
    /// <param name="x">Column or flat index.</param>
    /// <param name="y">Row; 0 for non-grid containers.</param>
    /// <param name="count">Stack count to place.</param>
    /// <returns>Status (implementation-specific failure codes, typically <see cref="ItemStatus.NotEnoughSpace"/>).</returns>
    ItemStatus TryPlace(GameItem item, short x, short y, short count);

    /// <summary>Auto-find a slot and place the item: stacks onto an existing same-template stack first (if stackable), else the first free position in scan order.</summary>
    /// <param name="item">Item being placed.</param>
    /// <param name="count">Stack count to place.</param>
    /// <returns>Status; <see cref="ItemStatus.NotEnoughSpace"/> when nothing fits.</returns>
    ItemStatus TryPlaceAuto(GameItem item, short count);

    /// <summary>Remove <paramref name="count"/> items from a slot; the slot is cleared when count reaches the stack size.</summary>
    /// <param name="x">Column or flat index.</param>
    /// <param name="y">Row; 0 for non-grid containers.</param>
    /// <param name="count">Number of items to remove.</param>
    /// <returns>Status; <see cref="ItemStatus.ItemNotFound"/> for an empty slot.</returns>
    ItemStatus Remove(short x, short y, short count);

    /// <summary>Check if an item can fit at a specific position without placing it.</summary>
    /// <param name="item">Item to test.</param>
    /// <param name="x">Column or flat index.</param>
    /// <param name="y">Row; 0 for non-grid containers.</param>
    /// <param name="count">Stack count to test.</param>
    /// <returns>True when <see cref="TryPlace"/> would succeed.</returns>
    bool CanFit(GameItem item, short x, short y, short count);

    /// <summary>
    /// Validate whether this container accepts the item at all, independent of position (e.g. equipment type check).
    /// Called by <see cref="IInventoryService"/> before adding, moving or swapping into the container. Default: true.
    /// </summary>
    /// <param name="item">Item to test.</param>
    /// <returns>False rejects the item with <see cref="ItemStatus.ValidationFailed"/>.</returns>
    bool ValidateItem(GameItem item) => true;

    /// <summary>Find the item instance in this container by its instance ID.</summary>
    /// <param name="itemInstanceId"><see cref="GameItem.InstanceId"/>.</param>
    /// <returns>The slot holding it (the anchor cell for grid items), or null.</returns>
    StorageSlot? FindItemSlot(string itemInstanceId);
}
