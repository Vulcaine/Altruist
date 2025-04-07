namespace Altruist.Gaming;

public class ItemStoreService : IItemStoreService
{
    private readonly ICacheProvider _cache;

    public ItemStoreService(ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
    }

    private string GetStorageKey(string storageId) => $"storage:{storageId}";

    public async Task<ItemStorageProvider?> FindStorageAsync(string storageId)
    {
        var key = GetStorageKey(storageId);
        var storage = await _cache.GetAsync<ItemStorage>(key);
        if (storage == null)
        {
            return null;
        }
        return new ItemStorageProvider(storage.StorageId, storage.MaxWidth, storage.MaxHeight, _cache);
    }

    public async Task SetItemAsync(SlotKey slotKey, long itemId, short itemCount)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);

        if (storage == null)
        {
            return;
        }

        await storage.SetItemAsync(itemId, itemCount, slotKey);
        await storage.SaveAsync();
    }

    public async Task MoveItemAsync(
     long itemId,
     SlotKey fromSlotKey,
     SlotKey toSlotKey,
     short count = 1
    )
    {
        var sourceStorage = await FindStorageAsync(fromSlotKey.StorageId);
        var targetStorage = await FindStorageAsync(toSlotKey.StorageId);
        if (sourceStorage == null || targetStorage == null)
            return;

        var sourceSlot = sourceStorage.RemoveItem(fromSlotKey, count);
        if (sourceSlot != null && Equals(fromSlotKey, toSlotKey))
        {
            // Move within same storage
            var success = await targetStorage.SetItemAsync(itemId, sourceSlot.ItemCount, toSlotKey);
            if (!success)
            {
                // Revert: put it back
                await sourceStorage.SetItemAsync(sourceSlot.ItemId, sourceSlot.ItemCount, fromSlotKey);
            }
        }

        await sourceStorage.SaveAsync();
    }

    public async Task<StorageItem?> RemoveItemAsync(SlotKey slotKey, short count = 1)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);
        if (storage == null)
            return null;
        var removed = storage.RemoveItem(slotKey, count);
        await storage.SaveAsync();
        return await storage.FindItemAsync(removed!.ItemId);
    }

    public async Task SortStorageAsync(string storageId)
    {
        var storage = await FindStorageAsync(storageId);
        if (storage == null)
            return;
        await storage.SortStorageAsync();
    }
}
