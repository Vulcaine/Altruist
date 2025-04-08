namespace Altruist.Gaming;

public class ItemStoreService : IItemStoreService
{
    private readonly ICacheProvider _cache;

    public ItemStoreService(ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
    }

    private string GetStorageKey(string storageId) => $"storage:{storageId}";

    public ItemStorageProvider CreateStorage(IStoragePrincipal principal, string storageId, (short Width, short Height) size, short slotCapacity = 1)
    {
        return new ItemStorageProvider(
            principal,
            storageId, size.Width, size.Height, slotCapacity, _cache);
    }

    public async Task<ItemStorageProvider?> FindStorageAsync(string storageId)
    {
        var key = GetStorageKey(storageId);
        var storage = await _cache.GetAsync<ItemStorage>(key);
        if (storage == null)
        {
            return null;
        }
        return new ItemStorageProvider(
            storage.Principal,
            storage.StorageId, storage.MaxWidth, storage.MaxHeight, storage.SlotCapacity, _cache);
    }

    public async Task SetItemAsync(SlotKey slotKey, string itemId, short itemCount)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);

        if (storage == null)
        {
            return;
        }

        await storage.SetItemAsync(itemId, itemCount, slotKey);
        await storage.SaveAsync();
    }

    public async Task<T?> MoveItemAsync<T>(
    string itemId,
    SlotKey fromSlotKey,
    SlotKey toSlotKey,
    short count = 1
) where T : GameItem
    {
        var sourceStorage = await FindStorageAsync(fromSlotKey.StorageId);
        var targetStorage = await FindStorageAsync(toSlotKey.StorageId);
        if (sourceStorage == null || targetStorage == null)
            return null;

        var removedSlots = sourceStorage.RemoveItem(fromSlotKey, count);
        if (removedSlots != null && removedSlots.Count > 0)
        {
            var success = await targetStorage.SetItemAsync(itemId, count, toSlotKey);
            if (!success)
            {
                // Revert the remove
                foreach (var slot in removedSlots)
                {
                    sourceStorage.RestoreSlot(slot);
                }
            }
        }

        await sourceStorage.SaveAsync();
        return await targetStorage.FindItemAsync<T>(itemId);
    }

    public async Task<T?> RemoveItemAsync<T>(SlotKey slotKey, short count = 1) where T : GameItem
    {
        var storage = await FindStorageAsync(slotKey.StorageId);
        if (storage == null)
            return null;
        var removed = storage.RemoveItem(slotKey, count);
        await storage.SaveAsync();
        if (removed.Count == 0)
        {
            return null;
        }
        return await storage.FindItemAsync<T>(removed.First().ItemInstanceId);
    }

    public async Task SortStorageAsync(string storageId)
    {
        var storage = await FindStorageAsync(storageId);
        if (storage == null)
            return;
        await storage.SortStorageAsync();
    }
}
