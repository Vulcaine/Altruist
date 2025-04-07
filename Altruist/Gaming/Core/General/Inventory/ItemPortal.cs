using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Represents a portal for managing inventory operations in the context of a player entity.
/// This abstract class facilitates the interaction with the inventory system through various operations
/// such as picking up, moving, dropping, and sorting items.
/// </summary>
/// <typeparam name="TPlayerEntity">The type of the player entity associated with the inventory. Must inherit from PlayerEntity.</typeparam>
public abstract class AltruistItemPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    /// <summary>
    /// The inventory service used to handle inventory operations like adding, moving, and removing items.
    /// </summary>
    protected readonly IItemStoreService _itemStoreService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AltruistItemPortal{TPlayerEntity}"/> class.
    /// </summary>
    /// <param name="context">The portal context, providing access to the current routing and cache systems.</param>
    /// <param name="itemStoreService">The inventory service that interacts with the inventory system.</param>
    /// <param name="loggerFactory">The logger factory for logging purposes.</param>
    protected AltruistItemPortal(IPortalContext context,
        GameWorldManager gameWorld,
        IItemStoreService itemStoreService,
        ILoggerFactory loggerFactory) : base(context, gameWorld, loggerFactory)
    {
        _itemStoreService = itemStoreService;
    }

    /// <summary>
    /// Handles the "destroy-item" request by removing an item and notifying nearby clients if necessary.
    /// </summary>
    /// <param name="packet">The packet representing the item to remove.</param>
    /// <param name="clientId">The client initiating the request.</param>
    [Gate("destroy-item")]
    public virtual async void DestroyItem(ItemRemovePacket packet, string clientId)
    {
        var removedItem = await _itemStoreService.RemoveItemAsync(packet.SlotKey);
        var updatedPacket = packet;

        if (packet.SlotKey.Id == "ground" && removedItem is WorldStorageItem worldStorageItem)
        {
            BroadcastToNearbyClients(worldStorageItem.WorldPosition.X, worldStorageItem.WorldPosition.Y, updatedPacket);
        }
        else
        {
            updatedPacket.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, updatedPacket);
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
        var movedItem = await _itemStoreService.MoveItemAsync(packet.ItemId, fromSlot, toSlot, packet.ItemCount);

        if (movedItem is WorldStorageItem worldStorageItem)
        {
            BroadcastToNearbyClients(worldStorageItem.WorldPosition.X, worldStorageItem.WorldPosition.Y, packet);
        }
        else
        {
            packet.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, packet);
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
        var movedItem = await _itemStoreService.MoveItemAsync(packet.ItemId, packet.SlotKey, packet.TargetSlotKey, packet.ItemCount);

        if (packet.TargetSlotKey.Id == "ground" && movedItem is WorldStorageItem worldStorageItem)
        {
            BroadcastToNearbyClients(worldStorageItem.WorldPosition.X, worldStorageItem.WorldPosition.Y, packet);
        }
        else
        {
            packet.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, packet);
        }
    }

    /// <summary>
    /// Handles the "sort-items" request by sorting the contents of a storage and returning the result to the client.
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
