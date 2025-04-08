namespace Altruist.Gaming;

public class ItemStorage
{
    public IStoragePrincipal Principal { get; }
    public string StorageId { get; set; }
    public Dictionary<SlotKey, StorageSlot> SlotMap { get; set; }

    public short MaxWidth { get; set; } = 10;
    public short MaxHeight { get; set; } = 10;

    public ItemStorage(
        IStoragePrincipal principal,
        string storageId, short maxWidth, short maxHeight)
    {
        Principal = principal;
        StorageId = storageId;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        SlotMap = new Dictionary<SlotKey, StorageSlot>();

        for (short y = 0; y < MaxHeight; y++)
        {
            for (short x = 0; x < MaxWidth; x++)
            {
                var slotKey = new SlotKey(x, y, id: storageId, storageId: storageId);
                var slot = new StorageSlot
                {
                    SlotKey = slotKey,
                    ItemCount = 0,
                    MaxCapacity = 1
                };
                SlotMap[slotKey] = slot;
            }
        }
    }

    public StorageSlot? FindSlot(SlotKey key)
    {
        SlotMap.TryGetValue(key, out var slot);
        return slot;
    }

    public void AddSlot(SlotKey key, StorageSlot slot)
    {
        SlotMap.Add(key, slot);
    }

    public void RemoveSlot(SlotKey key)
    {
        SlotMap.Remove(key);
    }

    public void Clear()
    {
        SlotMap.Clear();
    }
}

public class ItemStorageProvider
{
    private readonly ICacheProvider _cache;
    private readonly ItemStorage _storage;

    public ItemStorageProvider(
        IStoragePrincipal principal,
        string storageId, short maxWidth, short maxHeight, ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
        _storage = new ItemStorage(principal, storageId, maxWidth, maxHeight);
    }

    /// <summary>
    /// Finds an item by its id.
    /// </summary>
    /// <param name="itemId">The id of the item to find.</param>
    /// <returns>The item with the given id if it exists, null otherwise.</returns>
    public async Task<T?> FindItemAsync<T>(string itemId) where T : GameItem
    {
        return await _cache.GetAsync<T>(itemId);
    }

    public async Task<ICursor<T>> GetAllItemsAsync<T>() where T : GameItem
    {
        return await _cache.GetAllAsync<T>();
    }

    public StorageSlot? FindSlot(SlotKey key)
    {
        return _storage.FindSlot(key);
    }

    /// <summary>
    /// Attempts to set an item in the specified storage location.
    /// If coordinates (x, y) are provided, the item is placed at the specified location within the storage,
    /// provided there is enough space. If no coordinates are provided, the method will try to find a suitable
    /// space automatically. If the item cannot be placed, the method returns false.
    /// </summary>
    /// <param name="storageId">The identifier of the storage where the item should be placed.</param>
    /// <param name="itemId">The identifier of the item to place in the storage.</param>
    /// <param name="itemCount">The number of items to place in the storage.</param>
    /// <param name="x">The optional x-coordinate for placing the item. If not specified, auto-placement is attempted.</param>
    /// <param name="y">The optional y-coordinate for placing the item. If not specified, auto-placement is attempted.</param>
    /// <param name="slotId">The optional slot identifier for the storage location. Defaults to "inventory".</param>
    /// <returns>true if the item was successfully placed, false otherwise.</returns>
    public async Task<bool> SetItemAsync(string itemId, short itemCount, SlotKey slotKey)
    {
        var item = await FindItemAsync<GameItem>(itemId);
        if (item == null || item.Stackable == false && itemCount > 1)
            return false;

        return PlaceItemAt(slotKey, item, itemCount);
    }

    /// <summary>
    /// Adds the specified item to the storage if it has enough capacity.
    /// The item is placed in the first available slot that can accommodate its size.
    /// If no space is available, the item is not added and the method returns false.
    /// </summary>
    /// <param name="item">The item to be added to the storage.</param>
    /// <param name="itemCount">The number of items to add to the storage.</param>
    /// <param name="slotId">The slotId of the storage to add the item to. Defaults to "inventory".</param>
    /// <returns>true if the item was successfully added, false otherwise.</returns>
    public bool AddItem(GameItem item, short itemCount, string slotId)
    {
        for (short y = 0; y <= _storage.MaxHeight - item.Size.Y; y++)
        {
            for (short x = 0; x <= _storage.MaxWidth - item.Size.X; x++)
            {
                var atSlotKey = new SlotKey(x, y, slotId, _storage.StorageId);
                if (!CanFitAt(atSlotKey, item.Size.X, item.Size.Y))
                    continue;

                var positions = new List<SlotKey>();
                for (short dy = 0; dy < item.Size.Y; dy++)
                {
                    for (short dx = 0; dx < item.Size.X; dx++)
                    {
                        var slotX = (short)(x + dx);
                        var slotY = (short)(y + dy);
                        positions.Add(new SlotKey(slotX, slotY, slotId, _storage.StorageId));
                    }
                }

                return PlaceItemInternal(item.Id, itemCount, positions);
            }
        }

        return false;
    }

    public void RestoreSlot(StorageSlot slot)
    {
        _storage.SlotMap[slot.SlotKey] = slot;
    }

    private bool PlaceItemInternal(string itemId, short itemCount, List<SlotKey> positions)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            var current = positions[i];
            var next = positions[(i + 1) % positions.Count];

            var slot = _storage.SlotMap[current];

            slot.ItemInstanceId = itemId;
            slot.ItemCount += itemCount;
            slot.SlotLink = new IntVector2(next.X, next.Y);

            _storage.SlotMap[current] = slot;
        }

        return true;
    }


    private bool PlaceItemAt(SlotKey key, GameItem item, short itemCount)
    {
        if (!CanFitAt(key, item.Size.X, item.Size.Y))
            return false;

        var positions = new List<SlotKey>();
        for (short dy = 0; dy < item.Size.Y; dy++)
        {
            for (short dx = 0; dx < item.Size.X; dx++)
            {
                var slotX = (short)(key.X + dx);
                var slotY = (short)(key.Y + dy);
                positions.Add(new SlotKey(slotX, slotY, key.Id, _storage.StorageId));
            }
        }

        return PlaceItemInternal(item.Id, itemCount, positions);
    }



    private bool CanFitAt(SlotKey key, short width, short height)
    {
        var startX = key.X;
        var startY = key.Y;
        for (short dy = 0; dy < height; dy++)
        {
            for (short dx = 0; dx < width; dx++)
            {
                short x = (short)(startX + dx);
                short y = (short)(startY + dy);

                if (x >= _storage.MaxWidth || y >= _storage.MaxHeight)
                    return false;

                var atKey = new SlotKey(x, y, key.Id, _storage.StorageId);
                if (_storage.SlotMap.TryGetValue(atKey, out var slot) && slot.ItemCount > 0)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Removes the specified number of items from the slot at the given coordinates in the given storage.
    /// If the item count reaches zero, the item is removed from the storage.
    /// </summary>
    /// <param name="x">The x-coordinate of the slot.</param>
    /// <param name="y">The y-coordinate of the slot.</param>
    /// <param name="slotId">The slotId of the storage to remove the item from. Defaults to "inventory".</param>
    /// <param name="count">The number of items to remove. Defaults to 1.</param>
    /// <returns>true if the item was successfully removed, false otherwise.</returns>
    public List<StorageSlot> RemoveItem(SlotKey startKey, short count = 1)
    {
        if (!_storage.SlotMap.TryGetValue(startKey, out var startSlot) || startSlot.ItemCount < count)
            return new();

        var removed = new List<StorageSlot>();
        var visited = new HashSet<SlotKey>();
        var currentKey = startKey;

        do
        {
            if (!_storage.SlotMap.TryGetValue(currentKey, out var slot))
                break;

            var slotCopy = new StorageSlot
            {
                SlotKey = slot.SlotKey,
                ItemInstanceId = slot.ItemInstanceId,
                ItemCount = slot.ItemCount,
                MaxCapacity = slot.MaxCapacity,
                SlotLink = slot.SlotLink
            };
            slot.ItemCount -= count;

            if (slot.ItemCount <= 0)
            {
                slot.ItemInstanceId = "";
            }
            else
            {
                _storage.SlotMap[currentKey] = slot;
            }

            removed.Add(slotCopy);
            visited.Add(currentKey);

            if (slot.SlotLink is IntVector2 nextLink)
            {
                currentKey = new SlotKey(
                    (short)nextLink.X,
                    (short)nextLink.Y,
                    currentKey.Id,
                    currentKey.StorageId
                );
            }
            else break;

        } while (!visited.Contains(currentKey));

        return removed;
    }


    /// <summary>
    /// Moves an item from one location to another location in the same storage.
    /// This method first checks if the target area is available, then removes the item from its original position and places it in the new position.
    /// </summary>
    /// <param name="itemId">The id of the item to be moved.</param>
    /// <param name="toX">The x-coordinate of the target location.</param>
    /// <param name="toY">The y-coordinate of the target location.</param>
    /// <param name="fromSlotId">The slotId of the storage that the item is currently in. Defaults to "inventory".</param>
    /// <param name="slotId">The slotId of the storage that the item should be moved to. Defaults to "inventory".</param>
    /// <returns>true if the item was successfully moved, false otherwise.</returns>
    public async Task<bool> MoveItemAsync(string itemId, SlotKey fromSlotKey, SlotKey toSlotKey, short? count = 1)
    {
        if (count == null || count <= 0)
            return false;

        var item = await FindItemAsync<GameItem>(itemId);
        if (item == null)
            return false;

        if (!CanFitAt(toSlotKey, item.Size.X, item.Size.Y))
            return false;

        // Remove the full item from all linked slots
        var removedSlots = RemoveItem(fromSlotKey, count.Value);
        if (removedSlots == null || removedSlots.Count == 0)
            return false;

        // Add the item to the new location
        var success = await SetItemAsync(itemId, count ?? 1, toSlotKey);

        if (!success)
        {
            // Failed to place it, restore the original state (optional)
            foreach (var slot in removedSlots)
            {
                _storage.SlotMap[slot.SlotKey] = slot;
            }
            return false;
        }

        return true;
    }


    /// <summary>
    /// Sorts the items in the storage by category and saves the result.
    /// </summary>
    public async Task SortStorageAsync()
    {
        var itemTasks = _storage.SlotMap.Values.Select(e => FindItemAsync<GameItem>(e.ItemInstanceId)).ToList();
        var items = await Task.WhenAll(itemTasks);
        var sortedItems = items.Where(item => item != null).OrderBy(item => item!.Category).ToList();

        _storage.SlotMap.Clear();

        short index = 0;
        foreach (var item in sortedItems)
        {
            short x = (short)(index % _storage.MaxWidth);
            short y = (short)(index / _storage.MaxWidth);

            var key = new SlotKey(x, y, "inventory", _storage.StorageId);
            _storage.SlotMap[key] = new StorageSlot
            {
                SlotKey = key,
                ItemInstanceId = item!.Id,
                ItemCount = item.Count
            };

            index++;
        }
    }

    public async Task SaveAsync()
    {
        await _cache.SaveAsync(_storage.StorageId, _storage);
    }
}
