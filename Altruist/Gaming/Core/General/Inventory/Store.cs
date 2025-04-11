namespace Altruist.Gaming;

public class ItemStorage : IVaultModel
{
    public IStoragePrincipal Principal { get; }
    public string StorageId { get; set; }
    public Dictionary<SlotKey, StorageSlot> SlotMap { get; set; }

    public short MaxWidth { get; set; } = 10;
    public short MaxHeight { get; set; } = 10;

    public short SlotCapacity { get; set; } = 1;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "ItemStorage";

    public ItemStorage(
        IStoragePrincipal principal,
        string storageId, short maxWidth, short maxHeight, short capacity = 1)
    {
        Principal = principal;
        StorageId = storageId;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        SlotCapacity = capacity;

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
                    MaxCapacity = capacity
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
        string storageId, short maxWidth, short maxHeight, short slotCapacity, ICacheProvider cacheProvider)
    {
        _cache = cacheProvider;
        _storage = new ItemStorage(principal, storageId, maxWidth, maxHeight, slotCapacity);
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
    public async Task<SetItemStatus> SetItemAsync(string itemId, short itemCount, SlotKey slotKey)
    {
        var item = await FindItemAsync<GameItem>(itemId);
        if (item == null)
        {
            return SetItemStatus.ItemNotFound;
        }
        else if (!item.Stackable && itemCount > 1)
        {
            return SetItemStatus.NonStackable;
        }

        return SetItem(item, itemCount, slotKey);
    }

    private SetItemStatus SetItem(GameItem item, short itemCount, SlotKey slotKey)
    {
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
    public AddItemStatus AddItem(GameItem item, short itemCount, string slotId)
    {
        if (item.Stackable == false && itemCount > 1)
        {
            return AddItemStatus.NonStackable;
        }

        var startX = item.SlotKey.X;
        var startY = item.SlotKey.Y;
        for (short y = startY; y <= _storage.MaxHeight - item.Size.Y; y++)
        {
            for (short x = startX; x <= _storage.MaxWidth - item.Size.X; x++)
            {
                var atSlotKey = new SlotKey(x, y, slotId, _storage.StorageId);
                if (!CanFitAt(item, atSlotKey, item.Size.X, item.Size.Y, itemCount))
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

                PlaceItemInternal(item.Id, itemCount, positions);
                return AddItemStatus.Success;
            }
        }

        return AddItemStatus.NotEnoughSpace;
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


    private SetItemStatus PlaceItemAt(SlotKey key, GameItem item, short itemCount)
    {
        if (!CanFitAt(item, key, item.Size.X, item.Size.Y, itemCount))
            return SetItemStatus.NotEnoughSpace;

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

        PlaceItemInternal(item.Id, itemCount, positions);
        return SetItemStatus.Success;
    }



    private bool CanFitAt(GameItem item, SlotKey key, short width, short height, short itemCount)
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

                // We can stack only if we don't exceed max capacity and if the they are the same items
                // If the template id is the same, they must be the same!
                bool UnableToStackItem = _storage.SlotMap.TryGetValue(atKey, out var slot)
                    && (
                    slot.ItemTemplateId != 0 && slot.ItemTemplateId != item.TemplateId ||
                    item.Stackable && (slot.ItemCount + itemCount > slot.MaxCapacity)
                    || !item.Stackable && slot.ItemCount == 1);

                if (UnableToStackItem)
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
        if (count <= 0 || !_storage.SlotMap.TryGetValue(startKey, out var startSlot) || startSlot.ItemCount == 0)
            return new();

        var removeCount = Math.Min(count, startSlot.ItemCount);

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
            slot.ItemCount -= removeCount;

            if (slot.ItemCount <= 0)
            {
                slot.ClearSlot();
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

    public async Task<SwapSlotStatus> SwapSlotsAsync(SlotKey from, SlotKey to)
    {
        if (from.StorageId != to.StorageId)
        {
            throw new NotSupportedException("Cross-storage swap is not supported inside a storage.");
        }

        var removedFrom = RemoveItem(from, short.MaxValue);
        if (removedFrom.Count == 0) return SwapSlotStatus.CannotMove;
        var removedTo = RemoveItem(to, short.MaxValue);

        var firstSlotFrom = removedFrom.First();

        // rollback
        if (removedTo.Count == 0)
        {
            return (SwapSlotStatus)await SetItemAsync(firstSlotFrom.ItemInstanceId, firstSlotFrom.ItemCount, firstSlotFrom.SlotKey);
        }

        // Swap items:
        // Each item spans a grid of slots, and all slots reference the same item.
        // By taking the first slot (top-left), we get the anchor position of the item:
        // e.g. for a 2x2 item:
        //     |0,0|1,0|
        //     |0,1|1,1|
        // The top-left slot (0,0) is used to reposition the item via SetItemAsync, which
        // will handle filling in the rest of the grid.
        var firstSlotTo = removedTo.First();

        // TODO: must check if SetItem was successful
        await SetItemAsync(firstSlotFrom.ItemInstanceId, firstSlotFrom.ItemCount, to);
        await SetItemAsync(firstSlotTo.ItemInstanceId, firstSlotTo.ItemCount, from);

        return SwapSlotStatus.Success;
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
    public async Task<MoveItemStatus> MoveItemAsync(string itemId, SlotKey fromSlotKey, SlotKey toSlotKey, short? count = 1)
    {
        if (count == null || count <= 0)
            return MoveItemStatus.BadCount;

        var actualCount = count ?? 1;
        var item = await FindItemAsync<GameItem>(itemId);
        if (item == null)
            return MoveItemStatus.ItemNotFound;

        if (!CanFitAt(item, toSlotKey, item.Size.X, item.Size.Y, actualCount))
            return MoveItemStatus.NotEnoughSpace;

        // Remove the full item from all linked slots
        var removedSlots = RemoveItem(fromSlotKey, actualCount);
        if (removedSlots == null || removedSlots.Count == 0)
            return MoveItemStatus.CannotMove;

        // Add the item to the new location
        SetItemStatus statusCode = await SetItemAsync(itemId, actualCount, toSlotKey);

        if (statusCode != SetItemStatus.Success)
        {
            // Failed to place it, restore the original state (optional)
            foreach (var slot in removedSlots)
            {
                _storage.SlotMap[slot.SlotKey] = slot;
            }
            return (MoveItemStatus)statusCode;
        }

        return MoveItemStatus.Success;
    }

    /// <summary>
    /// Sorts the items in the storage using a sorting function
    /// </summary>
    public async Task<IEnumerable<StorageSlot>> SortStorageAsync(Func<List<SlotGroup>, Task<List<SlotGroup>>> sortFunc)
    {
        var slotMap = _storage.SlotMap;

        var groups = CollectGroups(slotMap);
        var sortedGroups = await SortGroupsAsync(groups, sortFunc);

        ReassignGroupsToStorage(slotMap, sortedGroups, _storage.MaxWidth);

        return slotMap.Values;
    }

    private List<SlotGroup> CollectGroups(Dictionary<SlotKey, StorageSlot> slotMap)
    {
        var visited = new HashSet<SlotKey>();
        var groups = new List<SlotGroup>();

        foreach (var slot in slotMap.Values)
        {
            if (visited.Contains(slot.SlotKey))
                continue;

            var groupSlots = new List<StorageSlot> { slot };
            visited.Add(slot.SlotKey);

            var current = slot;
            while (current.SlotLink.HasValue)
            {
                var nextKey = new SlotKey(
                    (short)current.SlotLink.Value.X,
                    (short)current.SlotLink.Value.Y,
                    current.SlotKey.Id,
                    current.SlotKey.StorageId);

                if (!slotMap.TryGetValue(nextKey, out var nextSlot))
                    break;

                groupSlots.Add(nextSlot);
                visited.Add(nextKey);
                current = nextSlot;
            }

            groups.Add(new SlotGroup(groupSlots));
        }

        return groups;
    }

    private void ReassignGroupsToStorage(
    Dictionary<SlotKey, StorageSlot> slotMap,
    List<SlotGroup> sortedGroups,
    short maxWidth)
    {
        short index = 0;
        foreach (var group in sortedGroups)
        {
            short baseX = (short)(index % maxWidth);
            short baseY = (short)(index / maxWidth);

            var layout = LayoutGridSlots(baseX, baseY, group.Width, group.Height);

            for (int i = 0; i < group.Slots.Count; i++)
            {
                var slot = group.Slots[i];
                var newKey = layout[i];

                slotMap[newKey] = slot;
                slot.SlotKey = newKey;

                if (i < group.Slots.Count - 1)
                {
                    var nextKey = layout[i + 1];
                    slot.SlotLink = new IntVector2(nextKey.X, nextKey.Y);
                }
                else
                {
                    slot.SlotLink = null;
                }
            }

            index += (short)(group.Width * group.Height);
        }
    }


    private async Task<List<SlotGroup>> SortGroupsAsync(List<SlotGroup> groups, Func<List<SlotGroup>, Task<List<SlotGroup>>> sortFunc)
    {
        return await sortFunc(groups);
    }

    private List<SlotKey> LayoutGridSlots(short startX, short startY, short width, short height)
    {
        var keys = new List<SlotKey>();

        for (short dy = 0; dy < height; dy++)
        {
            for (short dx = 0; dx < width; dx++)
            {
                var x = (short)(startX + dx);
                var y = (short)(startY + dy);
                keys.Add(new SlotKey(x, y, "inventory", _storage.StorageId));
            }
        }

        return keys;
    }


    public async Task SaveAsync()
    {
        await _cache.SaveAsync(_storage.StorageId, _storage);
    }
}
