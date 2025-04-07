namespace Altruist.Gaming;



public interface IItemStoreService
{
    Task SetItemAsync(
        SlotKey slotKey,
        long itemId,
        short itemCount
    );

    Task<StorageItem?> MoveItemAsync(
        long itemId,
        SlotKey fromSlotKey,
        SlotKey toSlotKey,
        short count = 1
    );

    Task<StorageItem?> RemoveItemAsync(
        SlotKey slotKey,
        short count = 1
    );

    Task<ItemStorageProvider?> FindStorageAsync(string storageId);

    Task SortStorageAsync(string storageId); // Optional, for InventorySortPacket
}
