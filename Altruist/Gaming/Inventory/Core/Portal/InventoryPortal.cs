/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Base inventory portal with default Gate handlers for all inventory operations.
/// Users extend this class and implement ResolvePlayerAsync to map clientId → player.
/// Virtual hooks allow customization of each operation.
/// </summary>
public abstract class AltruistInventoryPortal : Portal
{
    protected readonly IInventoryService InventoryService;
    protected readonly IAltruistRouter Router;
    protected readonly ILogger Logger;

    protected AltruistInventoryPortal(
        IInventoryService inventoryService,
        IAltruistRouter router,
        ILoggerFactory loggerFactory)
    {
        InventoryService = inventoryService;
        Router = router;
        Logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>Resolve the player entity from a client connection ID.</summary>
    protected abstract Task<PlayerEntity?> ResolvePlayerAsync(string clientId);

    /// <summary>Resolve the player ID (storage owner) from a client connection ID.</summary>
    protected abstract Task<string> ResolvePlayerIdAsync(string clientId);

    [Gate("move-item")]
    public virtual async Task OnMoveItem(CMoveItem packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.MoveItemAsync(packet.FromSlot, packet.ToSlot, packet.Count);
        result = await OnMoveItemCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    [Gate("pickup-item")]
    public virtual async Task OnPickupItem(CPickupItem packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.PickupItemAsync(playerId, packet.ItemInstanceId);
        result = await OnPickupCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    [Gate("drop-item")]
    public virtual async Task OnDropItem(CDropItem packet, string clientId)
    {
        var playerId = await ResolvePlayerIdAsync(clientId);
        var result = await InventoryService.DropItemAsync(playerId, packet.FromSlot);
        result = await OnDropCompleted(packet, clientId, result);
        await SendResultAsync(clientId, result.Status);
    }

    [Gate("equip-item")]
    public virtual async Task OnEquipItem(CEquipItem packet, string clientId)
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

    [Gate("unequip-item")]
    public virtual async Task OnUnequipItem(CUnequipItem packet, string clientId)
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

    [Gate("use-item")]
    public virtual async Task OnUseItem(CUseItem packet, string clientId)
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

    protected virtual Task<MoveItemResult> OnMoveItemCompleted(CMoveItem packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    protected virtual Task<MoveItemResult> OnPickupCompleted(CPickupItem packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    protected virtual Task<MoveItemResult> OnDropCompleted(CDropItem packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    protected virtual Task<MoveItemResult> OnEquipCompleted(CEquipItem packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    protected virtual Task<MoveItemResult> OnUnequipCompleted(CUnequipItem packet, string clientId, MoveItemResult result)
        => Task.FromResult(result);

    protected virtual Task<UseItemResult> OnUseItemCompleted(CUseItem packet, string clientId, UseItemResult result)
        => Task.FromResult(result);

    // ── Helpers ─────────────────────────────────────────────────────

    protected async Task SendResultAsync(string clientId, ItemStatus status)
    {
        await Router.Client.SendAsync(clientId, new SItemResult
        {
            Status = (int)status,
            Message = status.ToString()
        });
    }
}
