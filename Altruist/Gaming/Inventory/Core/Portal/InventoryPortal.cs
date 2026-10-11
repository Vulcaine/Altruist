/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Base inventory portal: ready-made <see cref="GateAttribute"/> handlers that turn client inventory packets
/// (<see cref="MoveItemPacket"/>, <see cref="PickupItemPacket"/>, <see cref="DropItemPacket"/>, <see cref="EquipItemPacket"/>,
/// <see cref="UnequipItemPacket"/>, <see cref="UseItemPacket"/>) into <see cref="IInventoryService"/> calls and reply
/// with an <see cref="ItemResultPacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// When to use: derive from it (and add <c>[Portal("/your-path")]</c>) when clients drive the inventory over the
/// realtime connection. Use <see cref="IInventoryService"/> directly for server-side logic (loot, rewards, admin tools)
/// that should not go through a client packet.
/// </para>
/// <para>
/// You must implement <see cref="ResolvePlayerIdAsync"/> (client id to container owner id) and <see cref="ResolvePlayerAsync"/>
/// (client id to <see cref="PlayerEntity"/>, used for <see cref="GameItem.OnEquip"/>/<see cref="GameItem.OnUnequip"/>/<see cref="GameItem.OnUse"/>).
/// Customize with the <c>On...Completed</c> hooks, which can inspect or replace the result before it is sent.
/// If you override a gate handler itself, re-apply the same <c>[Gate("...")]</c> attribute on the override:
/// <see cref="GateAttribute"/> is not inherited, so an override without it is no longer wired.
/// </para>
/// <para>
/// Gate event names: <c>move-item</c>, <c>pickup-item</c>, <c>drop-item</c>, <c>equip-item</c>, <c>unequip-item</c>, <c>use-item</c>.
/// The handlers do not check that slot keys in the packet belong to the resolved player; add such checks in an override
/// if clients are untrusted. Only the status is sent back; slot changes are not broadcast (send <see cref="SlotUpdatePacket"/>
/// from a hook if clients need them).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Portal("/game")]
/// public class MyInventoryPortal : AltruistInventoryPortal
/// {
///     private readonly IPlayerLookup _players; // your own lookup
///     public MyInventoryPortal(IInventoryService inv, IAltruistRouter router, ILoggerFactory lf, IPlayerLookup players)
///         : base(inv, router, lf) =&gt; _players = players;
///
///     protected override Task&lt;PlayerEntity?&gt; ResolvePlayerAsync(string clientId) =&gt; _players.ByClientAsync(clientId);
///     protected override async Task&lt;string&gt; ResolvePlayerIdAsync(string clientId)
///         =&gt; (await _players.ByClientAsync(clientId))?.Id ?? "";
///
///     protected override Task&lt;UseItemResult&gt; OnUseItemCompleted(UseItemPacket p, string clientId, UseItemResult r)
///     {
///         if (r.Status == ItemStatus.Success) InventoryService.RemoveItem(p.Slot, 1); // consume
///         return Task.FromResult(r);
///     }
/// }
/// </code>
/// </example>
public abstract class AltruistInventoryPortal : Portal
{
    /// <summary>The inventory service all handlers delegate to.</summary>
    protected readonly IInventoryService InventoryService;
    /// <summary>Router used to send the <see cref="ItemResultPacket"/> back to the client.</summary>
    protected readonly IAltruistRouter Router;
    /// <summary>Logger categorized by the concrete portal type.</summary>
    protected readonly ILogger Logger;

    /// <summary>Wires the portal to the inventory service and router (resolved from DI by the derived portal).</summary>
    /// <param name="inventoryService">Inventory service (default registration: the singleton <see cref="Altruist.Gaming.Inventory.InventoryService"/>).</param>
    /// <param name="router">Router used to reply to clients.</param>
    /// <param name="loggerFactory">Factory for <see cref="Logger"/>.</param>
    protected AltruistInventoryPortal(
        IInventoryService inventoryService,
        IAltruistRouter router,
        ILoggerFactory loggerFactory)
    {
        InventoryService = inventoryService;
        Router = router;
        Logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>
    /// Resolve the player entity from a client connection ID. Used only to invoke item callbacks
    /// (<see cref="GameItem.OnEquip"/>, <see cref="GameItem.OnUnequip"/>, <see cref="GameItem.OnUse"/>); return null to skip them.
    /// </summary>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <returns>The player entity, or null if unknown.</returns>
    protected abstract Task<PlayerEntity?> ResolvePlayerAsync(string clientId);

    /// <summary>
    /// Resolve the player ID (storage owner, i.e. <see cref="SlotKey.OwnerId"/> of the player's containers) from a client connection ID.
    /// </summary>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <returns>The owner id used when the containers were created via <see cref="IInventoryService.CreateContainer"/>.</returns>
    protected abstract Task<string> ResolvePlayerIdAsync(string clientId);

    /// <summary>
    /// Gate <c>move-item</c>: <see cref="IInventoryService.MoveItemAsync"/> from <see cref="MoveItemPacket.FromSlot"/> to
    /// <see cref="MoveItemPacket.ToSlot"/>, runs <see cref="OnMoveItemCompleted"/>, then replies with the status.
    /// The slot keys are used as sent (no ownership check).
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("move-item")]
    public virtual async Task OnMoveItem(MoveItemPacket packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.MoveItemAsync(packet.FromSlot, packet.ToSlot, packet.Count);
        result = await OnMoveItemCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    /// <summary>
    /// Gate <c>pickup-item</c>: <see cref="IInventoryService.PickupItemAsync"/> moves the ground item into the player's
    /// <c>"inventory"</c> container, runs <see cref="OnPickupCompleted"/>, then replies with the status.
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("pickup-item")]
    public virtual async Task OnPickupItem(PickupItemPacket packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.PickupItemAsync(playerId, packet.ItemInstanceId);
        result = await OnPickupCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    /// <summary>
    /// Gate <c>drop-item</c>: <see cref="IInventoryService.DropItemAsync"/> moves one item from the slot to the world container,
    /// runs <see cref="OnDropCompleted"/>, then replies with the status.
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("drop-item")]
    public virtual async Task OnDropItem(DropItemPacket packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.DropItemAsync(playerId, packet.FromSlot);
        result = await OnDropCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    /// <summary>
    /// Gate <c>equip-item</c>: <see cref="IInventoryService.EquipItemAsync"/> (named slot, or first compatible slot when
    /// <see cref="EquipItemPacket.EquipSlotName"/> is empty), calls <see cref="GameItem.OnEquip"/> on success when the player
    /// resolves, runs <see cref="OnEquipCompleted"/>, then replies with the status.
    /// When equipping swaps out an occupied slot, <see cref="GameItem.OnUnequip"/> is not called for the displaced item.
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("equip-item")]
    public virtual async Task OnEquipItem(EquipItemPacket packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var player = await ResolvePlayerAsync(clientId);

        var result = await InventoryService.EquipItemAsync(
            playerId, packet.FromSlot,
            string.IsNullOrEmpty(packet.EquipSlotName) ? null : packet.EquipSlotName);

        if (result.Status == ItemStatus.Success && result.Item != null && player != null)
            result.Item.OnEquip(player);

        result = await OnEquipCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    /// <summary>
    /// Gate <c>unequip-item</c>: <see cref="IInventoryService.UnequipItemAsync"/> moves the named equipment slot's item to the
    /// player's <c>"inventory"</c>, calls <see cref="GameItem.OnUnequip"/> on success when the player resolves,
    /// runs <see cref="OnUnequipCompleted"/>, then replies with the status.
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("unequip-item")]
    public virtual async Task OnUnequipItem(UnequipItemPacket packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var player = await ResolvePlayerAsync(clientId);

        // Get the item before unequipping to call OnUnequip
        var equipment = InventoryService.GetContainer(playerId, "equipment") as EquipmentStorage;
        var equipSlot = equipment?.GetSlotByName(packet.EquipSlotName);
        GameItem? oldItem = null;
        if (equipSlot != null && !equipSlot.IsEmpty)
            oldItem = InventoryService.GetItem(equipSlot.ItemInstanceId);

        var result = await InventoryService.UnequipItemAsync(playerId, packet.EquipSlotName);

        if (result.Status == ItemStatus.Success && oldItem != null && player != null)
            oldItem.OnUnequip(player);

        result = await OnUnequipCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    /// <summary>
    /// Gate <c>use-item</c>: <see cref="IInventoryService.UseItemAsync"/> checks the item exists and is not expired, calls
    /// <see cref="GameItem.OnUse"/> on success when the player resolves, runs <see cref="OnUseItemCompleted"/>, then replies.
    /// The item is not consumed; remove or decrement it in <see cref="OnUseItemCompleted"/> if it is a consumable.
    /// </summary>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="clientId">Connection id of the sender; the reply is sent here.</param>
    [Gate("use-item")]
    public virtual async Task OnUseItem(UseItemPacket packet, string clientId)
    {
        var player = await ResolvePlayerAsync(clientId);
        var result = await InventoryService.UseItemAsync(
            await ResolvePlayerIdAsync(clientId), packet.Slot);

        if (result.Status == ItemStatus.Success && result.Item != null && player != null)
            result.Item.OnUse(player);

        var finalResult = await OnUseItemCompleted(packet, clientId, result);
        await SendResultAsync(clientId, finalResult.Status);
    }

    // ── Virtual hooks ───────────────────────────────────────────────

    /// <summary>Hook after a move, before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<MoveItemResult> OnMoveItemCompleted(MoveItemPacket packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    /// <summary>Hook after a pickup, before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<MoveItemResult> OnPickupCompleted(PickupItemPacket packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    /// <summary>Hook after a drop, before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<MoveItemResult> OnDropCompleted(DropItemPacket packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    /// <summary>Hook after an equip (after <see cref="GameItem.OnEquip"/>), before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<MoveItemResult> OnEquipCompleted(EquipItemPacket packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    /// <summary>Hook after an unequip (after <see cref="GameItem.OnUnequip"/>), before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<MoveItemResult> OnUnequipCompleted(UnequipItemPacket packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    /// <summary>Hook after a use (after <see cref="GameItem.OnUse"/>), before the reply is sent. Override to send slot updates, persist, log, or replace the result (e.g. with <see cref="ItemStatus.CannotMove"/>). Changing the result does not undo the service operation.</summary>
    /// <param name="packet">The packet that triggered the operation.</param>
    /// <param name="clientId">Connection id of the sender.</param>
    /// <param name="result">Result from the service.</param>
    /// <returns>The result whose status is sent to the client (return <paramref name="result"/> unchanged by default).</returns>
    protected virtual Task<UseItemResult> OnUseItemCompleted(UseItemPacket packet, string clientId, UseItemResult result)
        => Task.FromResult(result);

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Sends an <see cref="ItemResultPacket"/> with the numeric status and its enum name to the client.</summary>
    /// <param name="clientId">Recipient connection id.</param>
    /// <param name="status">Status to report.</param>
    protected async Task SendResultAsync(string clientId, ItemStatus status)
    {
        await Router.Client.SendAsync(clientId, new ItemResultPacket
        {
            Status = (int)status,
            Message = status.ToString()
        });
    }
}
