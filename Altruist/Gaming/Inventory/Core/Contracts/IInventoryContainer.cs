/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Core container interface. Implemented by GridStorage, SlotStorage,
/// EquipmentStorage, and WorldItemStorage.
/// </summary>
public interface IInventoryContainer
{
    string ContainerId { get; }
    string OwnerId { get; }
    ContainerType ContainerType { get; }

    /// <summary>Get a specific slot by position.</summary>
    StorageSlot? GetSlot(short x, short y);

    /// <summary>Get all slots in this container.</summary>
    IReadOnlyCollection<StorageSlot> GetAllSlots();

    /// <summary>Get all non-empty slots.</summary>
    IEnumerable<StorageSlot> GetOccupiedSlots();

    /// <summary>Try to place an item at a specific position. Returns status.</summary>
    ItemStatus TryPlace(GameItem item, short x, short y, short count);

    /// <summary>Auto-find a slot and place the item. Returns status.</summary>
    ItemStatus TryPlaceAuto(GameItem item, short count);

    /// <summary>Remove count items from a slot. Returns status.</summary>
    ItemStatus Remove(short x, short y, short count);

    /// <summary>Check if an item can fit at a specific position.</summary>
    bool CanFit(GameItem item, short x, short y, short count);

    /// <summary>Validate whether this container accepts the item (e.g. equipment type check).</summary>
    bool ValidateItem(GameItem item) => true;

    /// <summary>Find the item instance in this container by its instance ID.</summary>
    StorageSlot? FindItemSlot(string itemInstanceId);
}
