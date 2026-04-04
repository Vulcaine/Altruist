/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// World ground items container. Unlimited slots, no size constraints.
/// Items dropped on the ground live here. Ephemeral (cache only, no DB persist by default).
/// </summary>
public class WorldItemStorage : IInventoryContainer
{
    private readonly Dictionary<(short X, short Y), StorageSlot> _slots = new();
    private short _nextIndex;

    public string ContainerId => "world";
    public string OwnerId { get; }
    public ContainerType ContainerType => ContainerType.Slot;

    public WorldItemStorage(string worldInstanceId)
    {
        OwnerId = worldInstanceId;
    }

    public StorageSlot? GetSlot(short x, short y)
        => _slots.TryGetValue((x, y), out var slot) ? slot : null;

    public IReadOnlyCollection<StorageSlot> GetAllSlots() => _slots.Values.ToList();

    public IEnumerable<StorageSlot> GetOccupiedSlots() => _slots.Values.Where(s => !s.IsEmpty);

    public StorageSlot? FindItemSlot(string itemInstanceId)
        => _slots.Values.FirstOrDefault(s => s.ItemInstanceId == itemInstanceId);

    public bool CanFit(GameItem item, short x, short y, short count) => true;

    public ItemStatus TryPlace(GameItem item, short x, short y, short count)
    {
        if (_slots.ContainsKey((x, y)) && !_slots[(x, y)].IsEmpty)
            return ItemStatus.NotEnoughSpace;

        var key = new SlotKey(x, y, ContainerId, OwnerId);
        var slot = new StorageSlot { SlotKey = key, MaxCapacity = 1 };
        slot.Set(item.InstanceId, item.TemplateId, count);
        _slots[(x, y)] = slot;
        return ItemStatus.Success;
    }

    public ItemStatus TryPlaceAuto(GameItem item, short count)
    {
        var idx = _nextIndex++;
        return TryPlace(item, idx, 0, count);
    }

    public ItemStatus Remove(short x, short y, short count)
    {
        if (!_slots.TryGetValue((x, y), out var slot) || slot.IsEmpty)
            return ItemStatus.ItemNotFound;

        _slots.Remove((x, y));
        return ItemStatus.Success;
    }

    public bool ValidateItem(GameItem item) => true;
}
