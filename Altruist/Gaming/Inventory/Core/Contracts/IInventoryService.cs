/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// High-level inventory service. Manages containers and provides
/// cross-container item operations (move, equip, pickup, drop, use).
/// </summary>
public interface IInventoryService
{
    // Container management
    IInventoryContainer CreateContainer(string ownerId, ContainerConfig config);
    IInventoryContainer? GetContainer(string ownerId, string containerId);
    void RemoveContainers(string ownerId);

    // The universal transfer operation
    Task<MoveItemResult> MoveItemAsync(SlotKey from, SlotKey to, short count = 1);
    Task<MoveItemResult> SwapItemsAsync(SlotKey slotA, SlotKey slotB);

    // Convenience operations
    Task<MoveItemResult> PickupItemAsync(string playerId, string itemInstanceId);
    Task<MoveItemResult> DropItemAsync(string playerId, SlotKey fromSlot);
    Task<MoveItemResult> EquipItemAsync(string playerId, SlotKey fromSlot, string? equipSlotName = null);
    Task<MoveItemResult> UnequipItemAsync(string playerId, string equipSlotName);
    Task<UseItemResult> UseItemAsync(string playerId, SlotKey slot);

    // Item CRUD
    GameItem CreateItem(long templateId, short count = 1);
    ItemStatus AddItem(string ownerId, string containerId, GameItem item, SlotKey? at = null);
    MoveItemResult RemoveItem(SlotKey slot, short count = 1);

    // Query
    GameItem? GetItem(string itemInstanceId);
    IEnumerable<StorageSlot> GetContainerSlots(string ownerId, string containerId);
}
