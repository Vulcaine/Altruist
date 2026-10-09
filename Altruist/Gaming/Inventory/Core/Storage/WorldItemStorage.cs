/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// World ground items container. Unlimited slots, no size constraints.
/// Items dropped on the ground live here. Ephemeral (cache only, no DB persist by default).
/// </summary>
/// <remarks>
/// When to use: loot and dropped items that players can pick up via <see cref="IInventoryService.PickupItemAsync"/>.
/// The container id is always <c>"world"</c> and the owner id is the world instance id.
/// <see cref="IInventoryService.DropItemAsync"/> creates and uses a single shared instance with owner <c>"world"</c>;
/// for per-instance ground items construct one per world and register it yourself. Slots are created on placement and
/// deleted on removal; auto placement uses an ever-increasing index (X), Y = 0. World position of the item is not stored
/// here; keep it in your own entity. Reports <see cref="ContainerType.Slot"/>.
/// </remarks>
public class WorldItemStorage : IInventoryContainer
{
    private readonly Dictionary<(short X, short Y), StorageSlot> _slots = new();
    private short _nextIndex;

    /// <summary>Always <c>"world"</c>.</summary>
    public string ContainerId => "world";
    /// <summary>The world instance id passed to the constructor.</summary>
    public string OwnerId { get; }
    /// <summary>Reports <see cref="ContainerType.Slot"/> (there is no dedicated world type).</summary>
    public ContainerType ContainerType => ContainerType.Slot;

    /// <summary>Creates an empty ground container.</summary>
    /// <param name="worldInstanceId">World instance id, used as <see cref="OwnerId"/>.</param>
    public WorldItemStorage(string worldInstanceId)
    {
        OwnerId = worldInstanceId;
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

    /// <summary>Always true (unbounded); <see cref="TryPlace"/> can still fail on an occupied position.</summary>
    /// <param name="item">Ignored.</param>
    /// <param name="x">Ignored.</param>
    /// <param name="y">Ignored.</param>
    /// <param name="count">Ignored.</param>
    /// <returns>Always true.</returns>
    public bool CanFit(GameItem item, short x, short y, short count) => true;

    /// <summary>Creates a slot at (x,y) holding the item; never stacks.</summary>
    /// <param name="item">Item to place.</param>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Row (normally 0).</param>
    /// <param name="count">Stack count.</param>
    /// <returns><see cref="ItemStatus.Success"/>, or <see cref="ItemStatus.NotEnoughSpace"/> when the position is occupied.</returns>
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

    /// <summary>Places at the next unused index (X), Y = 0; never stacks.</summary>
    /// <param name="item">Item to place.</param>
    /// <param name="count">Stack count.</param>
    /// <returns>Placement status.</returns>
    public ItemStatus TryPlaceAuto(GameItem item, short count)
    {
        var idx = _nextIndex++;
        return TryPlace(item, idx, 0, count);
    }

    /// <summary>Deletes the whole slot regardless of <paramref name="count"/>.</summary>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Row.</param>
    /// <param name="count">Ignored; the full stack is removed.</param>
    /// <returns><see cref="ItemStatus.Success"/> or <see cref="ItemStatus.ItemNotFound"/>.</returns>
    public ItemStatus Remove(short x, short y, short count)
    {
        if (!_slots.TryGetValue((x, y), out var slot) || slot.IsEmpty)
            return ItemStatus.ItemNotFound;

        _slots.Remove((x, y));
        return ItemStatus.Success;
    }

    /// <summary>Accepts every item.</summary>
    /// <param name="item">Item to test.</param>
    /// <returns>Always true.</returns>
    public bool ValidateItem(GameItem item) => true;
}
