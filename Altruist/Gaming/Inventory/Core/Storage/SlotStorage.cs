/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// WoW-style flat slot container. Each slot holds one item stack.
/// Items are always 1x1 in this mode.
/// </summary>
public class SlotStorage : IInventoryContainer
{
    private readonly Dictionary<(short X, short Y), StorageSlot> _slots = new();

    public string ContainerId { get; }
    public string OwnerId { get; }
    public ContainerType ContainerType => ContainerType.Slot;
    public short SlotCount { get; }
    public short SlotCapacity { get; }

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

    public StorageSlot? GetSlot(short x, short y)
        => _slots.TryGetValue((x, y), out var slot) ? slot : null;

    public IReadOnlyCollection<StorageSlot> GetAllSlots() => _slots.Values.ToList();

    public IEnumerable<StorageSlot> GetOccupiedSlots() => _slots.Values.Where(s => !s.IsEmpty);

    public StorageSlot? FindItemSlot(string itemInstanceId)
        => _slots.Values.FirstOrDefault(s => s.ItemInstanceId == itemInstanceId);

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

    public bool ValidateItem(GameItem item) => true;
}
