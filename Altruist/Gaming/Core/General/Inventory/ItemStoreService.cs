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

    public async Task<SwapSlotStatus> SwapSlotsAsync(SlotKey from, SlotKey to)
    {
        var fromStorage = await FindStorageAsync(from.StorageId);
        if (fromStorage == null)
            return SwapSlotStatus.StorageNotFound;

        // Same storage — simple swap
        if (from.StorageId == to.StorageId)
        {
            var status = await fromStorage.SwapSlotsAsync(from, to);
            await fromStorage.SaveAsync();
            return status;
        }

        // Cross-storage — fetch second storage
        var toStorage = await FindStorageAsync(to.StorageId);
        if (toStorage == null)
            return SwapSlotStatus.StorageNotFound;

        // Try remove both
        var sourceSlots = fromStorage.RemoveItem(from);
        var targetSlots = toStorage.RemoveItem(to);

        if (sourceSlots.Count == 0)
            return SwapSlotStatus.CannotMove;

        if (targetSlots.Count == 0)
        {
            // rollback source if target failed
            var rollback = sourceSlots.First();
            return (SwapSlotStatus)await fromStorage.SetItemAsync(rollback.ItemInstanceId, rollback.ItemCount, rollback.SlotKey);
        }

        // Swap items between storages:
        // Each item spans a grid of slots, and all slots reference the same item.
        // By taking the first slot (top-left), we get the anchor position of the item:
        // e.g. for a 2x2 item:
        //     |0,0|1,0|
        //     |0,1|1,1|
        // The top-left slot (0,0) is used to reposition the item via SetItemAsync, which
        // will handle filling in the rest of the grid.
        var itemFrom = sourceSlots.First();
        var itemTo = targetSlots.First();

        // TODO: must check if SetItem was successful
        await fromStorage.SetItemAsync(itemTo.ItemInstanceId, itemTo.ItemCount, from);
        await toStorage.SetItemAsync(itemFrom.ItemInstanceId, itemFrom.ItemCount, to);

        await fromStorage.SaveAsync();
        await toStorage.SaveAsync();

        return SwapSlotStatus.Success;
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

    public async Task<SetItemStatus> SetItemAsync(SlotKey slotKey, string itemId, short itemCount)
    {
        var storage = await FindStorageAsync(slotKey.StorageId);

        if (storage == null)
        {
            return SetItemStatus.StorageNotFound;
        }

        var status = await storage.SetItemAsync(itemId, itemCount, slotKey);
        await storage.SaveAsync();
        return status;
    }

    public async Task<(T? Item, MoveItemStatus Status)> MoveItemAsync<T>(
    string itemId,
    SlotKey fromSlotKey,
    SlotKey toSlotKey,
    short count = 1
) where T : GameItem
    {
        var sourceStorage = await FindStorageAsync(fromSlotKey.StorageId);
        var targetStorage = await FindStorageAsync(toSlotKey.StorageId);
        if (sourceStorage == null || targetStorage == null)
            return (null, MoveItemStatus.StorageNotFound);

        var removedSlots = sourceStorage.RemoveItem(fromSlotKey, count);
        if (removedSlots != null && removedSlots.Count > 0)
        {
            var statusCode = await targetStorage.SetItemAsync(itemId, count, toSlotKey);
            if (statusCode != SetItemStatus.Success)
            {
                // Revert the remove
                foreach (var slot in removedSlots)
                {
                    sourceStorage.RestoreSlot(slot);
                }

                return (null, (MoveItemStatus)statusCode);
            }
        }

        await sourceStorage.SaveAsync();
        return (await targetStorage.FindItemAsync<T>(itemId), MoveItemStatus.Success);
    }

    public async Task<(T? Item, RemoveItemStatus Status)> RemoveItemAsync<T>(SlotKey slotKey, short count = 1) where T : GameItem
    {
        var storage = await FindStorageAsync(slotKey.StorageId);
        if (storage == null)
            return (null, RemoveItemStatus.StorageNotFound);
        var removed = storage.RemoveItem(slotKey, count);
        await storage.SaveAsync();
        if (removed.Count == 0)
        {
            return (null, RemoveItemStatus.ItemNotFound);
        }
        return (await storage.FindItemAsync<T>(removed.First().ItemInstanceId), RemoveItemStatus.Success);
    }

    public async Task<IEnumerable<StorageSlot>> SortStorageAsync(
        string storageId,
        Func<List<SlotGroup>, Task<List<SlotGroup>>> sortFunc)
    {
        var storage = await FindStorageAsync(storageId);
        if (storage == null)
            return Enumerable.Empty<StorageSlot>();

        return await storage.SortStorageAsync(sortFunc);
    }

    public async Task<T?> FindItemAsync<T>(string storageId, SlotKey key) where T : GameItem
    {
        var storage = await FindStorageAsync(storageId);
        if (storage == null)
            return null;

        var slot = storage.FindSlot(key);

        if (slot == null)
        {
            return null;
        }

        return await storage.FindItemAsync<T>(slot.ItemInstanceId);
    }
}
