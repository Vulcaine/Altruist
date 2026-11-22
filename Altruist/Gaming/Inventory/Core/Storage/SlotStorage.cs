/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// WoW-style flat slot container. Each slot holds one item stack.
/// Items are always 1x1 in this mode.
/// </summary>
/// <remarks>
/// When to use: bags, banks, belts, hotbars, stash tabs where footprint does not matter. Use <see cref="GridStorage"/>
/// when items have sizes, <see cref="EquipmentStorage"/> for named equipment slots. Usually created by
/// <see cref="IInventoryService.CreateContainer"/> with <see cref="ContainerType.Slot"/>.
/// Slots are addressed as (index, 0) with index 0..SlotCount-1. Auto placement first tops up an existing same-template
/// stack that has room for the whole count, then uses the first empty slot.
/// </remarks>
public class SlotStorage : IInventoryContainer
{
    private readonly Dictionary<(short X, short Y), StorageSlot> _slots = new();

    /// <inheritdoc/>
    public string ContainerId { get; }
    /// <inheritdoc/>
    public string OwnerId { get; }
    /// <summary>Always <see cref="ContainerType.Slot"/>.</summary>
    public ContainerType ContainerType => ContainerType.Slot;
    /// <summary>Number of slots.</summary>
    public short SlotCount { get; }
    /// <summary>Maximum stack size of each slot.</summary>
    public short SlotCapacity { get; }

    /// <summary>Creates <paramref name="slotCount"/> empty slots. Prefer <see cref="IInventoryService.CreateContainer"/> so the service can find it.</summary>
    /// <param name="containerId">Container id.</param>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="slotCount">Number of slots.</param>
    /// <param name="slotCapacity">Maximum stack size per slot.</param>
    public SlotStorage(string containerId, string ownerId, short slotCount, short slotCapacity = 99)
    {
        ContainerId = containerId;
        OwnerId = ownerId;
        SlotCount = slotCount;
        SlotCapacity = slotCapacity;

        for (short i = 0; i < slotCount; i++)
        {
            var key = new SlotKey(i, 0, containerId, ownerId);
            _slots[(i, 0)] = new StorageSlot { SlotKey = key, MaxCapacity = slotCapacity };
        }
    }

    /// <inheritdoc/>
    public StorageSlot? GetSlot(short x, short y)
        => _slots.TryGetValue((x, y), out var slot) ? slot : null;

    /// <inheritdoc/>
    public IReadOnlyCollection<StorageSlot> GetAllSlots() => _slots.Values.ToList();

    /// <inheritdoc/>
    public IEnumerable<StorageSlot> GetOccupiedSlots() => _slots.Values.Where(s => !s.IsEmpty);

    /// <inheritdoc/>
    public StorageSlot? FindItemSlot(string itemInstanceId)
        => _slots.Values.FirstOrDefault(s => s.ItemInstanceId == itemInstanceId);

    /// <inheritdoc/>
    public bool CanFit(GameItem item, short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return false;

        if (slot.IsEmpty) return true;

        // Stacking: same template, stackable, room left
        if (item.Stackable && slot.ItemTemplateId == item.TemplateId)
            return slot.ItemCount + count <= slot.MaxCapacity;

        return false;
    }

    /// <summary>Places into an empty slot or stacks onto a same-template stackable item.</summary>
    /// <param name="item">Item to place.</param>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Must be 0.</param>
    /// <param name="count">Stack count to place.</param>
    /// <returns><see cref="ItemStatus.Success"/>, <see cref="ItemStatus.InvalidSlot"/>, <see cref="ItemStatus.StackFull"/> (would exceed capacity) or <see cref="ItemStatus.NotEnoughSpace"/> (occupied by something else).</returns>
    public ItemStatus TryPlace(GameItem item, short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return ItemStatus.InvalidSlot;

        if (slot.IsEmpty)
        {
            slot.Set(item.InstanceId, item.TemplateId, count);
            return ItemStatus.Success;
        }

        if (item.Stackable && slot.ItemTemplateId == item.TemplateId)
        {
            if (slot.ItemCount + count > slot.MaxCapacity)
                return ItemStatus.StackFull;

            slot.ItemCount += count;
            return ItemStatus.Success;
        }

        return ItemStatus.NotEnoughSpace;
    }

    /// <inheritdoc/>
    public ItemStatus TryPlaceAuto(GameItem item, short count)
    {
        // First try stacking with existing items of same template
        if (item.Stackable)
        {
            foreach (var slot in _slots.Values)
            {
                if (!slot.IsEmpty && slot.ItemTemplateId == item.TemplateId &&
                    slot.ItemCount + count <= slot.MaxCapacity)
                {
                    slot.ItemCount += count;
                    return ItemStatus.Success;
                }
            }
        }

        // Then find first empty slot
        foreach (var slot in _slots.Values)
        {
            if (slot.IsEmpty)
            {
                slot.Set(item.InstanceId, item.TemplateId, count);
                return ItemStatus.Success;
            }
        }

        return ItemStatus.NotEnoughSpace;
    }

    /// <inheritdoc/>
    public ItemStatus Remove(short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return ItemStatus.InvalidSlot;
        if (slot.IsEmpty) return ItemStatus.ItemNotFound;

        if (count >= slot.ItemCount)
        {
            slot.Clear();
        }
        else
        {
            slot.ItemCount -= count;
        }

        return ItemStatus.Success;
    }

    /// <summary>Accepts every item.</summary>
    /// <param name="item">Item to test.</param>
    /// <returns>Always true.</returns>
    public bool ValidateItem(GameItem item) => true;
}
