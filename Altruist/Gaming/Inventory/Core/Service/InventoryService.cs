/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Default in-memory <see cref="IInventoryService"/>. Holds every container (keyed <c>"{ownerId}:{containerId}"</c>) and
/// every tracked <see cref="GameItem"/> (keyed by instance id) in concurrent dictionaries; nothing is persisted.
/// </summary>
/// <remarks>
/// Registered as a singleton via <c>[Service(typeof(IInventoryService))]</c>; inject <see cref="IInventoryService"/>.
/// The dictionaries are thread-safe but container mutations are not, so serialize operations per owner.
/// All async methods complete synchronously. Containers are created through <see cref="CreateContainer"/>, except the
/// shared <c>"world"</c> ground container that <see cref="DropItemAsync"/> creates on demand.
/// </remarks>
[Service(typeof(IInventoryService))]
public class InventoryService : IInventoryService
{
    private readonly ConcurrentDictionary<string, IInventoryContainer> _containers = new();
    private readonly ConcurrentDictionary<string, GameItem> _items = new();
    private readonly IItemTemplateProvider _templates;

    /// <summary>Creates the service (normally resolved from DI).</summary>
    /// <param name="templates">Template source used by <see cref="CreateItem"/>.</param>
    public InventoryService(IItemTemplateProvider templates)
    {
        _templates = templates;
    }

    private static string ContainerKey(string ownerId, string containerId)
        => $"{ownerId}:{containerId}";

    // ── Container Management ────────────────────────────────────────

    /// <inheritdoc/>
    public IInventoryContainer CreateContainer(string ownerId, ContainerConfig config)
    {
        IInventoryContainer container = config.ContainerType switch
        {
            ContainerType.Grid => new GridStorage(
                config.ContainerId, ownerId, config.Width, config.Height, config.SlotCapacity),
            ContainerType.Slot => new SlotStorage(
                config.ContainerId, ownerId, config.SlotCount, config.SlotCapacity),
            ContainerType.Equipment => new EquipmentStorage(
                ownerId, config.EquipmentSlots ?? []),
            _ => throw new ArgumentException($"Unknown container type: {config.ContainerType}")
        };

        _containers[ContainerKey(ownerId, config.ContainerId)] = container;
        return container;
    }

    /// <inheritdoc/>
    public IInventoryContainer? GetContainer(string ownerId, string containerId)
        => _containers.TryGetValue(ContainerKey(ownerId, containerId), out var c) ? c : null;

    /// <inheritdoc/>
    public void RemoveContainers(string ownerId)
    {
        var keysToRemove = _containers.Keys.Where(k => k.StartsWith(ownerId + ":")).ToList();
        foreach (var key in keysToRemove)
            _containers.TryRemove(key, out _);
    }

    // ── Item CRUD ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public GameItem CreateItem(long templateId, short count = 1)
    {
        var template = _templates.GetTemplate(templateId)
            ?? throw new InvalidOperationException($"Item template {templateId} not found.");

        var item = template.CreateInstance(count);
        item.TemplateId = templateId;
        item.Count = count;
        _items[item.InstanceId] = item;
        return item;
    }

    /// <inheritdoc/>
    public GameItem? GetItem(string itemInstanceId)
        => _items.TryGetValue(itemInstanceId, out var item) ? item : null;

    /// <inheritdoc/>
    public ItemStatus AddItem(string ownerId, string containerId, GameItem item, SlotKey? at = null)
    {
        var container = GetContainer(ownerId, containerId);
        if (container == null) return ItemStatus.StorageNotFound;

        if (!container.ValidateItem(item))
            return ItemStatus.ValidationFailed;

        _items[item.InstanceId] = item;

        ItemStatus result;
        if (at.HasValue && !at.Value.IsAuto)
            result = container.TryPlace(item, at.Value.X, at.Value.Y, item.Count);
        else
            result = container.TryPlaceAuto(item, item.Count);

        if (result == ItemStatus.Success)
            item.CurrentSlot = at ?? SlotKey.Auto(containerId, ownerId);

        return result;
    }

    /// <inheritdoc/>
    public MoveItemResult RemoveItem(SlotKey slot, short count = 1)
    {
        var container = GetContainer(slot.OwnerId, slot.ContainerId);
        if (container == null) return new(ItemStatus.StorageNotFound);

        var existingSlot = container.GetSlot(slot.X, slot.Y);
        if (existingSlot == null || existingSlot.IsEmpty)
            return new(ItemStatus.ItemNotFound);

        var item = GetItem(existingSlot.ItemInstanceId);
        var status = container.Remove(slot.X, slot.Y, count);

        if (status == ItemStatus.Success)
        {
            // Storage holds the authoritative count on the slot; the GameItem
            // shadows it for callers that read item.Count directly. Keep them
            // in sync after a partial decrement, otherwise persistence/UI code
            // reading item.Count sees the pre-removal value.
            if (existingSlot.IsEmpty)
                _items.TryRemove(existingSlot.ItemInstanceId, out _);
            else if (item != null)
                item.Count = existingSlot.ItemCount;
        }

        return new(status, item);
    }

    // ── Universal Transfer ──────────────────────────────────────────

    /// <inheritdoc/>
    public Task<MoveItemResult> MoveItemAsync(SlotKey from, SlotKey to, short count = 1)
    {
        var srcContainer = GetContainer(from.OwnerId, from.ContainerId);
        if (srcContainer == null) return Task.FromResult(new MoveItemResult(ItemStatus.StorageNotFound));

        var dstContainer = GetContainer(to.OwnerId, to.ContainerId);
        if (dstContainer == null) return Task.FromResult(new MoveItemResult(ItemStatus.StorageNotFound));

        var srcSlot = srcContainer.GetSlot(from.X, from.Y);
        if (srcSlot == null || srcSlot.IsEmpty)
            return Task.FromResult(new MoveItemResult(ItemStatus.ItemNotFound));

        var item = GetItem(srcSlot.ItemInstanceId);
        if (item == null) return Task.FromResult(new MoveItemResult(ItemStatus.ItemNotFound));

        if (!dstContainer.ValidateItem(item))
            return Task.FromResult(new MoveItemResult(ItemStatus.ValidationFailed));

        // Check destination. For auto placement we can't use CanFit(0,0) — slot 0 might be
        // occupied while other slots are free. Defer the fit check to TryPlaceAuto below
        // and rollback via the existing source.TryPlace on failure.
        if (!to.IsAuto)
        {
            var placeResult = dstContainer.CanFit(item, to.X, to.Y, count) ? ItemStatus.Success : ItemStatus.NotEnoughSpace;
            if (placeResult != ItemStatus.Success)
                return Task.FromResult(new MoveItemResult(placeResult));
        }

        // Remove from source
        var removeStatus = srcContainer.Remove(from.X, from.Y, count);
        if (removeStatus != ItemStatus.Success)
            return Task.FromResult(new MoveItemResult(removeStatus));

        // Place in destination
        ItemStatus placeStatus;
        if (to.IsAuto)
            placeStatus = dstContainer.TryPlaceAuto(item, count);
        else
            placeStatus = dstContainer.TryPlace(item, to.X, to.Y, count);

        if (placeStatus != ItemStatus.Success)
        {
            // Rollback: put back in source
            srcContainer.TryPlace(item, from.X, from.Y, count);
            return Task.FromResult(new MoveItemResult(placeStatus));
        }

        return Task.FromResult(new MoveItemResult(ItemStatus.Success, item));
    }

    /// <inheritdoc/>
    public Task<MoveItemResult> SwapItemsAsync(SlotKey slotA, SlotKey slotB)
    {
        var containerA = GetContainer(slotA.OwnerId, slotA.ContainerId);
        var containerB = GetContainer(slotB.OwnerId, slotB.ContainerId);
        if (containerA == null || containerB == null)
            return Task.FromResult(new MoveItemResult(ItemStatus.StorageNotFound));

        var slotDataA = containerA.GetSlot(slotA.X, slotA.Y);
        var slotDataB = containerB.GetSlot(slotB.X, slotB.Y);
        if (slotDataA == null || slotDataB == null)
            return Task.FromResult(new MoveItemResult(ItemStatus.InvalidSlot));

        var itemA = slotDataA.IsEmpty ? null : GetItem(slotDataA.ItemInstanceId);
        var itemB = slotDataB.IsEmpty ? null : GetItem(slotDataB.ItemInstanceId);

        if (itemA == null && itemB == null)
            return Task.FromResult(new MoveItemResult(ItemStatus.ItemNotFound));

        // Validate cross-container compatibility (category, slot type, etc.)
        if (itemA != null && !containerB.ValidateItem(itemA))
            return Task.FromResult(new MoveItemResult(ItemStatus.ValidationFailed));
        if (itemB != null && !containerA.ValidateItem(itemB))
            return Task.FromResult(new MoveItemResult(ItemStatus.ValidationFailed));

        var countA = slotDataA.ItemCount;
        var countB = slotDataB.ItemCount;

        // Remove both endpoints first, THEN check fit. A pre-removal CanFit always
        // sees the source's anchor cell as occupied (by the very item we're about
        // to swap out), so any same-size swap — sword↔sword, helmet↔helmet — would
        // falsely return NotEnoughSpace and the equip path would fail with a deny.
        if (!slotDataA.IsEmpty) containerA.Remove(slotA.X, slotA.Y, countA);
        if (!slotDataB.IsEmpty) containerB.Remove(slotB.X, slotB.Y, countB);

        var placeB = itemB == null ? ItemStatus.Success : containerA.TryPlace(itemB, slotA.X, slotA.Y, countB);
        var placeA = itemA == null ? ItemStatus.Success : containerB.TryPlace(itemA, slotB.X, slotB.Y, countA);

        if (placeA != ItemStatus.Success || placeB != ItemStatus.Success)
        {
            // Roll back any partial placement, then restore originals.
            if (itemB != null && placeB == ItemStatus.Success) containerA.Remove(slotA.X, slotA.Y, countB);
            if (itemA != null && placeA == ItemStatus.Success) containerB.Remove(slotB.X, slotB.Y, countA);

            if (itemA != null) containerA.TryPlace(itemA, slotA.X, slotA.Y, countA);
            if (itemB != null) containerB.TryPlace(itemB, slotB.X, slotB.Y, countB);

            return Task.FromResult(new MoveItemResult(
                placeA != ItemStatus.Success ? placeA : placeB));
        }

        return Task.FromResult(new MoveItemResult(ItemStatus.Success, itemA));
    }

    // ── Convenience Operations ──────────────────────────────────────

    /// <inheritdoc/>
    public Task<MoveItemResult> PickupItemAsync(string playerId, string itemInstanceId)
    {
        // Find item in any world container
        foreach (var kv in _containers)
        {
            if (!kv.Key.Contains(":world")) continue;

            var worldContainer = kv.Value;
            var slot = worldContainer.FindItemSlot(itemInstanceId);
            if (slot != null)
            {
                var from = slot.SlotKey;
                var to = SlotKey.Auto("inventory", playerId);
                return MoveItemAsync(from, to, slot.ItemCount);
            }
        }

        return Task.FromResult(new MoveItemResult(ItemStatus.ItemNotFound));
    }

    /// <inheritdoc/>
    public Task<MoveItemResult> DropItemAsync(string playerId, SlotKey fromSlot)
    {
        // Find or create world container
        var worldKey = ContainerKey("world", "world");
        if (!_containers.ContainsKey(worldKey))
        {
            _containers[worldKey] = new WorldItemStorage("world");
        }

        var to = SlotKey.Auto("world", "world");
        return MoveItemAsync(fromSlot, to, 1);
    }

    /// <inheritdoc/>
    public Task<MoveItemResult> EquipItemAsync(string playerId, SlotKey fromSlot, string? equipSlotName = null)
    {
        var item = GetItemAtSlot(fromSlot);
        if (item == null) return Task.FromResult(new MoveItemResult(ItemStatus.ItemNotFound));

        SlotKey to;
        if (!string.IsNullOrEmpty(equipSlotName))
        {
            var equipment = GetContainer(playerId, "equipment") as EquipmentStorage;
            if (equipment == null) return Task.FromResult(new MoveItemResult(ItemStatus.StorageNotFound));

            to = equipment.GetSlotKeyByName(equipSlotName);
            if (to == default) return Task.FromResult(new MoveItemResult(ItemStatus.InvalidSlot));

            // If slot is occupied, swap
            var targetSlot = equipment.GetSlot(to.X, to.Y);
            if (targetSlot != null && !targetSlot.IsEmpty)
                return SwapItemsAsync(fromSlot, to);
        }
        else
        {
            to = SlotKey.Auto("equipment", playerId);
        }

        return MoveItemAsync(fromSlot, to, 1);
    }

    /// <inheritdoc/>
    public Task<MoveItemResult> UnequipItemAsync(string playerId, string equipSlotName)
    {
        var equipment = GetContainer(playerId, "equipment") as EquipmentStorage;
        if (equipment == null) return Task.FromResult(new MoveItemResult(ItemStatus.StorageNotFound));

        var fromKey = equipment.GetSlotKeyByName(equipSlotName);
        if (fromKey == default) return Task.FromResult(new MoveItemResult(ItemStatus.InvalidSlot));

        var to = SlotKey.Auto("inventory", playerId);
        return MoveItemAsync(fromKey, to, 1);
    }

    /// <inheritdoc/>
    public Task<UseItemResult> UseItemAsync(string playerId, SlotKey slot)
    {
        var item = GetItemAtSlot(slot);
        if (item == null) return Task.FromResult(new UseItemResult(ItemStatus.ItemNotFound));

        if (item.IsExpired) return Task.FromResult(new UseItemResult(ItemStatus.ItemExpired));

        // OnUse is called by the portal after getting the player entity
        return Task.FromResult(new UseItemResult(ItemStatus.Success, item));
    }

    // ── Query ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IEnumerable<StorageSlot> GetContainerSlots(string ownerId, string containerId)
    {
        var container = GetContainer(ownerId, containerId);
        return container?.GetAllSlots() ?? [];
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private GameItem? GetItemAtSlot(SlotKey slot)
    {
        var container = GetContainer(slot.OwnerId, slot.ContainerId);
        var slotData = container?.GetSlot(slot.X, slot.Y);
        if (slotData == null || slotData.IsEmpty) return null;
        return GetItem(slotData.ItemInstanceId);
    }
}
