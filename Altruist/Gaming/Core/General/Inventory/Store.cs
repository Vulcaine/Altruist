using Altruist.Database;
using Microsoft.VisualBasic;

namespace Altruist.Gaming;

public class ItemStorage
{
    private readonly ICacheProvider _cache;

    public string StorageId { get; set; }
    public Dictionary<SlotKey, StorageSlot> SlotMap { get; set; } = new();

    public short MaxWidth { get; set; } = 10;
    public short MaxHeight { get; set; } = 10;

    public ItemStorage(string storageId, short maxWidth, short maxHeight, ICacheProvider cacheProvider)
    {
        StorageId = storageId;
        _cache = cacheProvider;
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
    }

    // Returns the key for a slot based on its coordinates (x, y) and optional slotId
    private SlotKey GetSlotKey(short x, short y, string slotId = "inventory") =>
        new SlotKey(x, y, slotId, StorageId);

    // Ensures that we do not exceed the storage capacity by checking the grid boundaries
    private bool IsSlotAvailable(short x, short y, int width, int height, string slotId)
    {
        for (short i = x; i < x + width; i++)
        {
            for (short j = y; j < y + height; j++)
            {
                if (i >= MaxWidth || j >= MaxHeight || SlotMap.ContainsKey(GetSlotKey(i, j, slotId)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Finds an item by its id.
    /// </summary>
    /// <param name="itemId">The id of the item to find.</param>
    /// <returns>The item with the given id if it exists, null otherwise.</returns>
    public async Task<InventoryItem?> FindItemAsync(long itemId)
    {
        return await _cache.GetAsync<InventoryItem>($"item:{StorageId}:{itemId}");
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
    public async Task<bool> SetItemAsync(long itemId, short itemCount, SlotKey slotKey)
    {
        var item = await FindItemAsync(itemId);

        if (item == null)
            return false;

        // If position is specified
        if (!CanFitAt(slotKey, item.Width, item.Height))
            return false;

        PlaceItemAt(slotKey, item, itemCount);
        return true;
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
    public bool AddItemAsync(InventoryItem item, short itemCount, string slotId)
    {
        for (short y = 0; y <= MaxHeight - item.Height; y++)
        {
            for (short x = 0; x <= MaxWidth - item.Width; x++)
            {
                var atSlotKey = new SlotKey(x, y, slotId, StorageId);
                if (!CanFitAt(atSlotKey, item.Width, item.Height))
                    continue;

                // Reserve all the slots for the item
                for (short dy = 0; dy < item.Height; dy++)
                {
                    for (short dx = 0; dx < item.Width; dx++)
                    {
                        short slotX = (short)(x + dx);
                        short slotY = (short)(y + dy);
                        SlotKey key = new SlotKey(slotX, slotY, slotId, StorageId);

                        SlotMap[key] = new StorageSlot
                        {

                            ItemId = item.Id,
                            ItemCount = itemCount,
                            SlotKey = key
                        };
                    }
                }

                return true;
            }
        }

        return false;
    }

    private void PlaceItemAt(SlotKey key, InventoryItem item, short itemCount)
    {
        var startX = key.X;
        var startY = key.Y;
        for (short dy = 0; dy < item.Height; dy++)
        {
            for (short dx = 0; dx < item.Width; dx++)
            {
                short x = (short)(startX + dx);
                short y = (short)(startY + dy);

                SlotKey atKey = new SlotKey(x, y, key.Id, StorageId);
                SlotMap[key] = new StorageSlot
                {
                    ItemId = item.Id,
                    ItemCount = itemCount,
                    SlotKey = atKey
                };
            }
        }
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

                if (x >= MaxWidth || y >= MaxHeight)
                    return false;

                var atKey = new SlotKey(x, y, key.Id, StorageId);
                if (SlotMap.ContainsKey(atKey))
                    return false;
            }
        }

        return true;
    }

    // public StorageSlot? RemoveItemAsync(SlotKey slotKey, short count = 1)
    // {
    //     if (SlotMap.TryGetValue(slotKey, out var slot))
    //     {
    //         return RemoveItemAsync(slotKey, count);
    //     }

    //     return null;
    // }

    /// <summary>
    /// Removes the specified number of items from the slot at the given coordinates in the given storage.
    /// If the item count reaches zero, the item is removed from the storage.
    /// </summary>
    /// <param name="x">The x-coordinate of the slot.</param>
    /// <param name="y">The y-coordinate of the slot.</param>
    /// <param name="slotId">The slotId of the storage to remove the item from. Defaults to "inventory".</param>
    /// <param name="count">The number of items to remove. Defaults to 1.</param>
    /// <returns>true if the item was successfully removed, false otherwise.</returns>
    public StorageSlot? RemoveItemAsync(SlotKey key, short count = 1)
    {
        // string key = GetSlotKey(x, y, slotId);
        var slot = SlotMap.GetValueOrDefault(key);
        if (slot == null || slot.ItemCount < count)
        {
            return null!;
        }

        slot.ItemCount -= count;

        if (slot.ItemCount == 0)
        {
            SlotMap.Remove(key);
        }

        return slot;
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
    public async Task<bool> MoveItemAsync(long itemId, SlotKey fromSlotKey, SlotKey toSlotKey, short? count = 1)
    {
        if (count == null || count <= 0)
            return false;

        var item = await FindItemAsync(itemId);
        if (item == null)
            return false;

        if (!SlotMap.TryGetValue(fromSlotKey, out var fromSlot) || !SlotMap.TryGetValue(toSlotKey, out var toSlot))
            return false;

        if (fromSlot.ItemId != itemId || fromSlot.ItemCount < count)
            return false;

        // Ensure we're not exceeding MaxCapacity in the target slot
        if (toSlot.ItemId != 0 && toSlot.ItemId != itemId)
            return false;

        if (toSlot.ItemCount + count > toSlot.MaxCapacity)
            return false;

        // Remove count from fromSlot
        fromSlot.ItemCount -= count.Value;
        if (fromSlot.ItemCount <= 0)
        {
            fromSlot.ItemId = 0;
            fromSlot.ItemCount = 0;
        }
        SlotMap[fromSlotKey] = fromSlot;

        toSlot.ItemId = itemId;
        toSlot.ItemCount += count.Value;
        SlotMap[toSlotKey] = toSlot;

        return true;
    }


    /// <summary>
    /// Sorts the items in the storage by category and saves the result.
    /// </summary>
    public async Task SortStorageAsync()
    {
        var itemTasks = SlotMap.Values.Select(e => FindItemAsync(e.ItemId)).ToList();
        var items = await Task.WhenAll(itemTasks);
        var sortedItems = items.Where(item => item != null).OrderBy(item => item!.Category).ToList();

        SlotMap.Clear();

        short index = 0;
        foreach (var item in sortedItems)
        {
            short x = (short)(index % MaxWidth);
            short y = (short)(index / MaxWidth);

            var key = new SlotKey(x, y, "inventory", StorageId);
            SlotMap[key] = new StorageSlot
            {
                SlotKey = key,
                ItemId = item!.Id,
                ItemCount = item.Count
            };

            index++;
        }
    }

    public async Task SaveAsync()
    {
        await _cache.SaveAsync(StorageId, this);
    }
}
