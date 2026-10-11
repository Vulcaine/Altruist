/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// High-level inventory service. Manages containers and provides
/// cross-container item operations (move, equip, pickup, drop, use).
/// </summary>
/// <remarks>
/// <para>
/// When to use: this is the entry point for all server-side inventory logic. Call it instead of mutating an
/// <see cref="IInventoryContainer"/> directly, because the service also tracks the <see cref="GameItem"/> instances
/// by id (containers only store ids and counts) and validates/rolls back cross-container moves.
/// For client-driven operations, derive a portal from <see cref="AltruistInventoryPortal"/>, which calls this service.
/// </para>
/// <para>
/// Default implementation <see cref="InventoryService"/> is registered as a singleton via <c>[Service]</c>. All state is
/// in memory (no persistence); containers are not locked, so serialize operations on the same owner (e.g. run them on
/// the game tick or per-player queue). The async methods complete synchronously.
/// </para>
/// <para>
/// Conventions: containers are keyed by (owner id, container id). The convenience operations assume the container ids
/// <c>"inventory"</c> (pickup/unequip destination), <c>"equipment"</c> (equip target) and <c>"world"</c> (ground items).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// inventory.CreateContainer(playerId, new ContainerConfig { ContainerId = "inventory", ContainerType = ContainerType.Slot, SlotCount = 30 });
/// var potion = inventory.CreateItem(templateId: 1001, count: 5);
/// var status = inventory.AddItem(playerId, "inventory", potion);           // auto-placed
/// var moved  = await inventory.MoveItemAsync(new SlotKey(0, 0, "inventory", playerId),
///                                            new SlotKey(4, 0, "inventory", playerId), count: 2);
/// </code>
/// </example>
public interface IInventoryService
{
    // Container management
    /// <summary>
    /// Creates a container of <see cref="ContainerConfig.ContainerType"/> for <paramref name="ownerId"/> and registers it under
    /// (<paramref name="ownerId"/>, <see cref="ContainerConfig.ContainerId"/>), replacing any existing container with that key
    /// (items it held are not migrated). Call once per owner/container, e.g. when a player session starts.
    /// </summary>
    /// <param name="ownerId">Owner id (player, guild, world instance).</param>
    /// <param name="config">Container layout.</param>
    /// <returns>The new container.</returns>
    /// <exception cref="ArgumentException">Unknown <see cref="ContainerType"/>.</exception>
    IInventoryContainer CreateContainer(string ownerId, ContainerConfig config);
    /// <summary>Looks up a registered container.</summary>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="containerId">Container id.</param>
    /// <returns>The container, or null when none is registered.</returns>
    IInventoryContainer? GetContainer(string ownerId, string containerId);
    /// <summary>
    /// Unregisters every container of <paramref name="ownerId"/> (e.g. on logout after persisting). Tracked
    /// <see cref="GameItem"/> instances are not removed from the item registry.
    /// </summary>
    /// <param name="ownerId">Owner id.</param>
    void RemoveContainers(string ownerId);

    // The universal transfer operation
    /// <summary>
    /// Moves <paramref name="count"/> items from one slot to another, within or across containers and owners.
    /// Validates with <see cref="IInventoryContainer.ValidateItem"/> and, for a concrete destination,
    /// <see cref="IInventoryContainer.CanFit"/>; then removes from the source and places at the destination, putting the
    /// items back in the source if placement fails. Use <see cref="SlotKey.Auto(string, string)"/> as <paramref name="to"/>
    /// for first-free-slot placement. To exchange two occupied slots use <see cref="SwapItemsAsync"/> instead
    /// (a move onto an occupied, non-stackable slot fails with <see cref="ItemStatus.NotEnoughSpace"/>).
    /// </summary>
    /// <param name="from">Source slot; must hold an item (for multi-cell grid items pass the anchor, top-left, cell).</param>
    /// <param name="to">Destination slot or auto key.</param>
    /// <param name="count">Number of items to move from the stack. Not validated against the stack size.</param>
    /// <returns>Status and, on success, the moved item.</returns>
    Task<MoveItemResult> MoveItemAsync(SlotKey from, SlotKey to, short count = 1);
    /// <summary>
    /// Exchanges the full contents of two slots (either may be empty, but not both), within or across containers.
    /// Both items are validated against the other container, both slots are emptied, then each item is placed in the
    /// other slot; on failure everything is restored. Use for drag-onto-occupied-slot and equip-replace.
    /// </summary>
    /// <param name="slotA">First slot (concrete, not auto).</param>
    /// <param name="slotB">Second slot (concrete, not auto).</param>
    /// <returns>Status and, on success, the item that was in <paramref name="slotA"/> (null if it was empty).</returns>
    Task<MoveItemResult> SwapItemsAsync(SlotKey slotA, SlotKey slotB);

    // Convenience operations
    /// <summary>
    /// Finds the item in any registered world container (container id <c>"world"</c>) and moves its whole stack to
    /// the player's <c>"inventory"</c> container (auto placement).
    /// </summary>
    /// <param name="playerId">Owner id of the destination inventory.</param>
    /// <param name="itemInstanceId"><see cref="GameItem.InstanceId"/> of the ground item.</param>
    /// <returns>Move result; <see cref="ItemStatus.ItemNotFound"/> when no world container holds the item.</returns>
    Task<MoveItemResult> PickupItemAsync(string playerId, string itemInstanceId);
    /// <summary>
    /// Moves one item from <paramref name="fromSlot"/> to the shared ground container (owner <c>"world"</c>, container
    /// <c>"world"</c>, a <see cref="WorldItemStorage"/> created on first use). For per-world-instance ground items create
    /// your own <see cref="WorldItemStorage"/> container and use <see cref="MoveItemAsync"/> instead.
    /// </summary>
    /// <param name="playerId">Dropping player (currently not used to validate <paramref name="fromSlot"/>).</param>
    /// <param name="fromSlot">Slot to drop from.</param>
    /// <returns>Move result.</returns>
    Task<MoveItemResult> DropItemAsync(string playerId, SlotKey fromSlot);
    /// <summary>
    /// Moves the item at <paramref name="fromSlot"/> into the player's <c>"equipment"</c> container
    /// (<see cref="EquipmentStorage"/>). With <paramref name="equipSlotName"/> the named slot is used and, if occupied,
    /// swapped via <see cref="SwapItemsAsync"/>; without it the first compatible empty slot is used.
    /// Does not call <see cref="GameItem.OnEquip"/>; the portal (or caller) does.
    /// </summary>
    /// <param name="playerId">Owner of the equipment container.</param>
    /// <param name="fromSlot">Slot holding the item.</param>
    /// <param name="equipSlotName">Target equipment slot name, or null/empty for auto.</param>
    /// <returns>Move result; <see cref="ItemStatus.InvalidSlot"/> for an unknown slot name, <see cref="ItemStatus.StorageNotFound"/> without an equipment container.</returns>
    Task<MoveItemResult> EquipItemAsync(string playerId, SlotKey fromSlot, string? equipSlotName = null);
    /// <summary>
    /// Moves the item in the named equipment slot to the player's <c>"inventory"</c> container (auto placement).
    /// Does not call <see cref="GameItem.OnUnequip"/>; the portal (or caller) does.
    /// </summary>
    /// <param name="playerId">Owner of the equipment and inventory containers.</param>
    /// <param name="equipSlotName">Equipment slot name (case-insensitive).</param>
    /// <returns>Move result.</returns>
    Task<MoveItemResult> UnequipItemAsync(string playerId, string equipSlotName);
    /// <summary>
    /// Checks that <paramref name="slot"/> holds a tracked, non-expired item and returns it. Has no side effects:
    /// the caller applies <see cref="GameItem.OnUse"/> and consumes the item (e.g. <see cref="RemoveItem"/>) if needed.
    /// </summary>
    /// <param name="playerId">Using player (currently not used to validate <paramref name="slot"/>).</param>
    /// <param name="slot">Slot holding the item.</param>
    /// <returns><see cref="ItemStatus.Success"/> with the item, <see cref="ItemStatus.ItemNotFound"/> or <see cref="ItemStatus.ItemExpired"/>.</returns>
    Task<UseItemResult> UseItemAsync(string playerId, SlotKey slot);

    // Item CRUD
    /// <summary>
    /// Creates a new item instance from a template registered in <see cref="IItemTemplateProvider"/> via
    /// <see cref="ItemTemplate.CreateInstance"/>, sets its TemplateId and Count, and tracks it. The item is not placed
    /// anywhere; follow with <see cref="AddItem"/>.
    /// </summary>
    /// <param name="templateId"><see cref="ItemTemplate.ItemId"/>.</param>
    /// <param name="count">Stack count.</param>
    /// <returns>The new, tracked item.</returns>
    /// <exception cref="InvalidOperationException">No template with that id.</exception>
    GameItem CreateItem(long templateId, short count = 1);
    /// <summary>
    /// Tracks <paramref name="item"/> and places its full <see cref="GameItem.Count"/> into a container: at
    /// <paramref name="at"/> when given (and not auto), otherwise at the first free / stackable slot. Use for loot, rewards
    /// and loading persisted inventories; use <see cref="MoveItemAsync"/> for items already in a container.
    /// </summary>
    /// <param name="ownerId">Container owner id.</param>
    /// <param name="containerId">Container id.</param>
    /// <param name="item">Item to add (e.g. from <see cref="CreateItem"/>).</param>
    /// <param name="at">Optional concrete slot (only X/Y are used).</param>
    /// <returns>Placement status; <see cref="ItemStatus.StorageNotFound"/> or <see cref="ItemStatus.ValidationFailed"/> before placement.</returns>
    ItemStatus AddItem(string ownerId, string containerId, GameItem item, SlotKey? at = null);
    /// <summary>
    /// Removes <paramref name="count"/> items from a slot (all of them if count &gt;= stack). Keeps
    /// <see cref="GameItem.Count"/> in sync on a partial removal and stops tracking the item once its slot empties.
    /// Use for consuming, destroying or selling; use <see cref="MoveItemAsync"/> to relocate.
    /// </summary>
    /// <param name="slot">Slot to remove from.</param>
    /// <param name="count">Number of items to remove.</param>
    /// <returns>Status and the affected item.</returns>
    MoveItemResult RemoveItem(SlotKey slot, short count = 1);

    // Query
    /// <summary>Returns a tracked item by its <see cref="GameItem.InstanceId"/>.</summary>
    /// <param name="itemInstanceId">Instance id (e.g. <see cref="StorageSlot.ItemInstanceId"/>).</param>
    /// <returns>The item, or null when not tracked.</returns>
    GameItem? GetItem(string itemInstanceId);
    /// <summary>
    /// Returns every slot of a container (including empty and linked grid cells), e.g. to send a full snapshot or persist it.
    /// </summary>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="containerId">Container id.</param>
    /// <returns>The slots, or an empty sequence when the container does not exist.</returns>
    IEnumerable<StorageSlot> GetContainerSlots(string ownerId, string containerId);
}
