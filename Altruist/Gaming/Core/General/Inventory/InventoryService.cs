namespace Altruist.Gaming;


public class ItemStoreService : IItemStoreService
{
    private readonly ICacheProvider _cache;

    public ItemStoreService(ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
    }

    private string GetStorageKey(string storageId) => $"storage:{storageId}";
    private SlotKey GetSlotKey(short x, short y, string slotId = "inventory", string storageId = "inventory") => new SlotKey(
        x, y, slotId, storageId
    );

    public async Task<ItemStorage?> FindStorageAsync(string storageId)
    {
        var key = GetStorageKey(storageId);
        var storage = await _cache.GetAsync<ItemStorage>(key);
        if (storage == null)
        {
            return null;
        }
        return storage;
    }

    public async Task SetItemAsync(SlotKey slotKey, long itemId, short itemCount)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);

        if (storage == null)
        {
            return;
        }

        storage.SlotMap[slotKey] = new StorageSlot
        {
            SlotKey = slotKey,
            ItemId = itemId,
            ItemCount = itemCount
        };

        await _cache.SaveAsync(GetStorageKey(slotKey.StorageId), storage);
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

        var sourceSlot = sourceStorage.RemoveItemAsync(fromSlotKey, count);
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

    public async Task<StorageSlot?> RemoveItemAsync(SlotKey slotKey, short count = 1)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);
        if (storage == null)
            return null;
        return storage.RemoveItemAsync(slotKey, count);
    }

    public async Task UseItemAsync(SlotKey slot, long itemId)
    {
        var storage = await FindStorageAsync(slot.StorageId);

        if (storage == null)
            return;

        var kvp = storage.SlotMap.FirstOrDefault(kvp => kvp.Value.ItemId == itemId);
        storage.SlotMap.Remove(kvp.Key);
        await _cache.SaveAsync(GetStorageKey(slot.StorageId), storage);
    }

    public async Task SortStorageAsync(string storageId)
    {
        var storage = await FindStorageAsync(storageId);
        if (storage == null)
            return;
        await storage.SortStorageAsync();
    }
}
