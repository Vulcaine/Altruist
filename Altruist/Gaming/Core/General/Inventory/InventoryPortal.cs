using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Represents a portal for managing inventory operations in the context of a player entity.
/// This abstract class facilitates the interaction with the inventory system through various operations
/// such as picking up, moving, dropping, and sorting items.
/// </summary>
/// <typeparam name="TPlayerEntity">The type of the player entity associated with the inventory. Must inherit from PlayerEntity.</typeparam>
public abstract class AltruistInventoryPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    /// <summary>
    /// The inventory service used to handle inventory operations like adding, moving, and removing items.
    /// </summary>
    protected readonly IItemStoreService _itemStoreService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AltruistInventoryPortal{TPlayerEntity}"/> class.
    /// </summary>
    /// <param name="context">The portal context, providing access to the current routing and cache systems.</param>
    /// <param name="itemStoreService">The inventory service that interacts with the inventory system.</param>
    /// <param name="loggerFactory">The logger factory for logging purposes.</param>
    protected AltruistInventoryPortal(IPortalContext context,
        GameWorldCoordinator worldCoordinator,
        IPlayerService<TPlayerEntity> playerService,
        IItemStoreService itemStoreService,
        ILoggerFactory loggerFactory) : base(context, worldCoordinator, playerService, loggerFactory)
    {
        _itemStoreService = itemStoreService;
    }

    private async Task RemoveItemFromWorldAndNotifyClient(GameItem item, string clientId)
    {
        var world = await FindWorldForClientAsync(clientId);
        if (world != null)
        {
            var removedObject = world.DestroyObject(WorldObjectTypeKeys.Item, item.Id + "");
            if (removedObject != null)
            {
                _ = DispatchDestroyItemPacket(item.Id + "", clientId);
            }
        }
    }

    private async Task DispatchDestroyItemPacket(string objectId, string clientId)
    {
        var room = await FindRoomForClientAsync(clientId);
        if (room != null)
        {
            var destroyPacket = new DestroyObjectPacket("server", objectId);
            _ = Router.Room.SendAsync(room.Id, destroyPacket);
        }
    }

    /// <summary>
    /// Handles the "destroy-item" request by removing an item and notifying nearby clients if necessary.
    /// </summary>
    /// <param name="packet">The packet representing the item to remove.</param>
    /// <param name="clientId">The client initiating the request.</param>
    [Gate("destroy-item")]
    public virtual async void DestroyItem(ItemRemovePacket packet, string clientId)
    {
        var status = await _itemStoreService.RemoveItemAsync<GameItem>(packet.SlotKey);
        if (packet.SlotKey.Id == "ground" && status.Item != null)
        {
            _ = RemoveItemFromWorldAndNotifyClient(status.Item, clientId);
        }
        else if (status.Item != null)
        {
            packet.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, packet);
        }
        else
        {
            _ = Router.Client.SendAsync(clientId, PacketHelper.Failed("Item not found", packet.Type, clientId));
        }
    }

    /// <summary>
    /// Handles the "pickup-item" request by setting an item in the player's inventory.
    /// Broadcasts the pickup to nearby players if the item was on the ground.
    /// </summary>
    /// <param name="packet">The packet describing the pickup action.</param>
    /// <param name="clientId">The client performing the action.</param>
    [Gate("pickup-item")]
    public virtual async void PickupItem(ItemPickUpPacket packet, string clientId)
    {
        var fromSlot = SlotKeys.InventoryAnyPos;
        var toSlot = SlotKeys.GroundAnyPos;
        var status = await _itemStoreService.MoveItemAsync<GameItem>(packet.ItemId, fromSlot, toSlot, packet.ItemCount);

        if (status.Item != null)
        {
            _ = RemoveItemFromWorldAndNotifyClient(status.Item, clientId);
        }
        else
        {
            _ = Router.Client.SendAsync(clientId, PacketHelper.Failed("Item not found", packet.Type, clientId));
        }
    }

    /// <summary>
    /// Handles the "move-item" request by moving an item between storages.
    /// Broadcasts the move to nearby players if the item is dropped to the ground.
    /// </summary>
    /// <param name="packet">The move request packet.</param>
    /// <param name="clientId">The client performing the move.</param>
    [Gate("move-item")]
    public virtual async void MoveItem(InventoryMoveItemPacket packet, string clientId)
    {
        var status = await _itemStoreService.MoveItemAsync<GameItem>(packet.ItemId, packet.SlotKey, packet.TargetSlotKey, packet.ItemCount);

        if (status.Item != null && packet.TargetSlotKey.Id == "ground")
        {
            _ = RemoveItemFromWorldAndNotifyClient(status.Item, clientId);
        }
        else if (status.Item != null)
        {
            packet.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, packet);
        }
        else
        {
            _ = Router.Client.SendAsync(clientId, PacketHelper.Failed("Item not found", packet.Type, clientId));
        }
    }

    /// <summary>
    /// Handles the "sort-items" request by sorting the contents of a storage and notifying the client as well
    /// </summary>
    /// <param name="packet">The sort request packet.</param>
    /// <param name="clientId">The client making the request.</param>
    [Gate("sort-items")]
    public virtual async Task SortItems(InventorySortPacket packet, string clientId)
    {
        await _itemStoreService.SortStorageAsync(packet.StorageId);
        packet.Header = new PacketHeader("server", clientId);
        _ = Router.Client.SendAsync(clientId, packet);
    }
}
