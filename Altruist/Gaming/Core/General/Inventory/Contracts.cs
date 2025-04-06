namespace Altruist.Gaming;



public interface IInventoryService
{
    Task SetItemAsync(
        string storageId,
        long itemId,
        short itemCount,
        short? x = null,
        short? y = null,
        string? slotId = "inventory"
    );

    Task MoveItemAsync(
        long itemId,
        string storageId,
        string targetStorageId,
        short x,
        short y,
        string? fromSlotId = "inventory", 
        string? slotId = "inventory"
    );

    Task RemoveItemAsync(
        string storageId,
        int x,
        int y,
        string? slotId = "inventory"
    );

    Task UseItemAsync(
        string storageId,
        long itemId
    );

    Task<InventoryStorage> GetStorageAsync(string storageId);

    Task SortStorageAsync(string storageId); // Optional, for InventorySortPacket
}
