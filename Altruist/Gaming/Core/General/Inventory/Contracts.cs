namespace Altruist.Gaming;



public interface IItemStoreService
{
    Task SetItemAsync(
        SlotKey slotKey,
        long itemId,
        short itemCount
    );

    Task MoveItemAsync(
        long itemId,
        SlotKey fromSlotKey,
        SlotKey toSlotKey,
        short count = 1
    );

    Task<StorageSlot?> RemoveItemAsync(
        SlotKey slotKey,
        short count = 1
    );

    Task UseItemAsync(
        SlotKey slot,
        long itemId
    );

    Task<ItemStorage?> FindStorageAsync(string storageId);

    Task SortStorageAsync(string storageId); // Optional, for InventorySortPacket
}
