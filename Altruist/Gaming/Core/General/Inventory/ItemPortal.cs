using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Represents a portal for managing inventory operations in the context of a player entity.
/// This abstract class facilitates the interaction with the inventory system through various operations
/// such as picking up, moving, dropping, and sorting items.
/// </summary>
/// <typeparam name="TPlayerEntity">The type of the player entity associated with the inventory. Must inherit from PlayerEntity.</typeparam>
public abstract class AltruistItemPortal<TPlayerEntity> : Portal where TPlayerEntity : PlayerEntity, new()
{
    /// <summary>
    /// The inventory service used to handle inventory operations like adding, moving, and removing items.
    /// </summary>
    protected readonly IItemStoreService _itemStoreService;

    protected readonly GameWorld _world;

    /// <summary>
    /// Initializes a new instance of the <see cref="AltruistItemPortal{TPlayerEntity}"/> class.
    /// </summary>
    /// <param name="context">The portal context, providing access to the current routing and cache systems.</param>
    /// <param name="itemStoreService">The inventory service that interacts with the inventory system.</param>
    /// <param name="loggerFactory">The logger factory for logging purposes.</param>
    protected AltruistItemPortal(IPortalContext context, IItemStoreService itemStoreService, GameWorld gameWorld, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _itemStoreService = itemStoreService;
        _world = gameWorld;
    }

    [Gate("destroy-item")]
    public virtual async void DestroyItem(ItemRemovePacket packet, string clientId)
    {
        await _itemStoreService.RemoveItemAsync(packet.SlotKey);
        var updatedPacket = packet;
        if (packet.SlotKey.Id == "ground")
        {
            updatedPacket.Header = new PacketHeader("server");
            var playerRoom = await FindRoomForClientAsync(clientId);
            if (playerRoom != null)
            {
                _ = Router.Room.SendAsync(playerRoom.Id, updatedPacket);
            }
        }
        else
        {
            updatedPacket.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, updatedPacket);
        }
    }

    /// <summary>
    /// Handles the "pickup-item" request by setting an item in the player's inventory.
    /// This method updates the inventory state and sends a packet with the updated item data to other players in the room.
    /// </summary>
    /// <param name="packet">The packet containing information about the item to be picked up.</param>
    /// <param name="clientId">The unique identifier of the client making the request.</param>
    [Gate("pickup-item")]
    public virtual async void PickupItem(ItemPickUpPacket packet, string clientId)
    {
        var fromSlot = new SlotKey(-1, -1, "ground", "ground");
        var toSlot = new SlotKey(-1, -1, "inventory", "inventory");
        await _itemStoreService.MoveItemAsync(packet.ItemId, fromSlot, toSlot, packet.ItemCount);
        var playerRoom = await FindRoomForClientAsync(clientId);

        // Notify all players, all clients must remove picked up items from the ground.
        if (playerRoom != null)
        {
            var updatedPacket = packet;
            updatedPacket.Header = new PacketHeader("server");
            _ = Router.Room.SendAsync(playerRoom.Id, updatedPacket);
        }
    }

    /// <summary>
    /// Handles the "move-item" request by moving an item from one storage location to another.
    /// This method updates the inventory and sends the updated packet back to the client.
    /// </summary>
    /// <param name="packet">The packet containing information about the item to be moved.</param>
    /// <param name="clientId">The unique identifier of the client making the request.</param>
    [Gate("move-item")]
    public virtual async void MoveItem(InventoryMoveItemPacket packet, string clientId)
    {
        await _itemStoreService.MoveItemAsync(packet.ItemId, packet.SlotKey, packet.TargetSlotKey, packet.ItemCount);
        var updatedPacket = packet;

        // if target is ground, we are dropping the item, should notify everyone around
        if (packet.TargetSlotKey.Id == "ground")
        {
            updatedPacket.Header = new PacketHeader("server");
            var playerRoom = await FindRoomForClientAsync(clientId);
            if (playerRoom != null)
            {
                _ = Router.Room.SendAsync(playerRoom.Id, updatedPacket);
            }
        }
        else
        {
            updatedPacket.Header = new PacketHeader("server", clientId);
            _ = Router.Client.SendAsync(clientId, updatedPacket);
        }
    }

    /// <summary>
    /// Handles the "sort-items" request to sort items in a given storage. 
    /// This method updates the inventory sorting order and informs the client about the updated state.
    /// </summary>
    /// <param name="packet">The packet containing information about the sorting operation.</param>
    /// <param name="clientId">The unique identifier of the client making the request.</param>
    [Gate("sort-items")]
    public virtual async Task SortItems(InventorySortPacket packet, string clientId)
    {
        await _itemStoreService.SortStorageAsync(packet.StorageId);
        var updatedPacket = packet;
        updatedPacket.Header = new PacketHeader("server", clientId);
        _ = Router.Client.SendAsync(clientId, updatedPacket);
    }
}
