namespace Altruist.Gaming;



public interface IItemStoreService
{
    Task SetItemAsync(
        SlotKey slotKey,
        long itemId,
        short itemCount
    );

    Task<GameItem?> MoveItemAsync(
        long itemId,
        SlotKey fromSlotKey,
        SlotKey toSlotKey,
        short count = 1
    );

    Task<GameItem?> RemoveItemAsync(
        SlotKey slotKey,
        short count = 1
    );

    Task<ItemStorageProvider?> FindStorageAsync(string storageId);

    Task SortStorageAsync(string storageId); // Optional, for InventorySortPacket
}
