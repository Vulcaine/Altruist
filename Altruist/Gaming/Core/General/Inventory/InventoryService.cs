namespace Altruist.Gaming;


public class InventoryService : IInventoryService
{
    private readonly ICacheProvider _cache;

    public InventoryService(ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
    }

    private string GetKey(string storageId) => $"storage:{storageId}";
    private string GetSlotKey(string storageId, int x, int y, string? slotId = "inventory") => $"slot:{storageId}:{slotId}:{x}:{y}";

    public async Task<InventoryStorage> GetStorageAsync(string storageId)
    {
        var key = GetKey(storageId);
        var storage = await _cache.GetAsync<InventoryStorage>(key);
        if (storage == null)
        {
            storage = new InventoryStorage(storageId);
            await _cache.SaveAsync(key, storage);
        }
        return storage;
    }

    public async Task SetItemAsync(string storageId, long itemId, short itemCount, short? x = null, short? y = null,  string? slotId = "inventory")
    {
        var storage = await GetStorageAsync(storageId);
        string key = GetSlotKey(storageId, x ?? 0, y ?? 0, slotId);

        storage.SlotMap[key] = new StorageSlot
        {
            X = x ?? 0,
            Y = y ?? 0,
            StorageId = storageId,
            SlotId = key,
            ItemId = itemId,
            ItemCount = itemCount
        };

        await _cache.SaveAsync(GetKey(storageId), storage);
    }

    public async Task MoveItemAsync(long itemId, string storageId, string targetStorageId, short x, short y, string? fromSlotId = "inventory",  string? slotId = "inventory")
    {
        // Search item in all storages
        var currentStorage = await GetStorageAsync(storageId);
        var targetStorage = await GetStorageAsync(targetStorageId);
       
        if (currentStorage == null || targetStorage == null)
            return;

        var currentKey = GetSlotKey(storageId, x, y, fromSlotId);

        if (currentKey == null) {
            return;
        }

        var item = currentStorage.SlotMap[currentKey];
        currentStorage.SlotMap.Remove(currentKey);
        await _cache.SaveAsync(GetKey(currentStorage.StorageId), currentStorage);

        var newKey = GetSlotKey(targetStorageId, x, y, slotId);
        targetStorage.SlotMap[newKey] = new StorageSlot
        {
            X = x,
            Y = y,
            SlotId = newKey,
            ItemId = item.ItemId,
            ItemCount = item.ItemCount
        };

        await _cache.SaveAsync(GetKey(targetStorageId), targetStorage);
    }

    public async Task RemoveItemAsync(string storageId, int x, int y, string? slotId = "inventory")
    {
        var storage = await GetStorageAsync(storageId);
        string key = GetSlotKey(storageId, x, y, slotId);
        storage.SlotMap.Remove(key);
        await _cache.SaveAsync(GetKey(storageId), storage);
    }

    public async Task UseItemAsync(string storageId, long itemId)
    {
        var storage = await GetStorageAsync(storageId);
        var kvp = storage.SlotMap.FirstOrDefault(kvp => kvp.Value.ItemId == itemId);
        if (!string.IsNullOrEmpty(kvp.Key))
        {
            storage.SlotMap.Remove(kvp.Key);
            await _cache.SaveAsync(GetKey(storageId), storage);
        }
    }

    public async Task SortStorageAsync(string storageId)
    {
        var storage = await GetStorageAsync(storageId);
        var sorted = storage.SlotMap.Values.OrderBy(s => s.ItemId).ToList();

        storage.SlotMap.Clear();

        short index = 0;
        foreach (var slot in sorted)
        {
            short x = (short)(index % 10);
            short y = (short)(index / 10);
            var key = $"{x}:{y}";

            storage.SlotMap[key] = new StorageSlot
            {
                X = x,
                Y = y,
                SlotId = null,
                ItemId = slot.ItemId,
                ItemCount = slot.ItemCount
            };
            index++;
        }

        await _cache.SaveAsync(GetKey(storageId), storage);
    }
}
