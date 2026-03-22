/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Diablo-style grid container. Items can occupy multiple cells (e.g. 2x3 sword).
/// Multi-cell items use SlotLink to chain occupied cells back to the anchor (top-left).
/// </summary>
public class GridStorage : IInventoryContainer
{
    private readonly StorageSlot[,] _grid;

    public string ContainerId { get; }
    public string OwnerId { get; }
    public ContainerType ContainerType => ContainerType.Grid;
    public short Width { get; }
    public short Height { get; }
    public short SlotCapacity { get; }

    public GridStorage(string containerId, string ownerId, short width, short height, short slotCapacity = 99)
    {
        ContainerId = containerId;
        OwnerId = ownerId;
        Width = width;
        Height = height;
        SlotCapacity = slotCapacity;

        _grid = new StorageSlot[width, height];
        for (short x = 0; x < width; x++)
        {
            for (short y = 0; y < height; y++)
            {
                _grid[x, y] = new StorageSlot
                {
                    SlotKey = new SlotKey(x, y, containerId, ownerId),
                    MaxCapacity = slotCapacity
                };
            }
        }
    }

    public StorageSlot? GetSlot(short x, short y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return null;
        return _grid[x, y];
    }

    public IReadOnlyCollection<StorageSlot> GetAllSlots()
    {
        var list = new List<StorageSlot>(Width * Height);
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                list.Add(_grid[x, y]);
        return list;
    }

    public IEnumerable<StorageSlot> GetOccupiedSlots()
    {
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                if (!_grid[x, y].IsEmpty)
                    yield return _grid[x, y];
    }

    public StorageSlot? FindItemSlot(string itemInstanceId)
    {
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                if (_grid[x, y].ItemInstanceId == itemInstanceId && !_grid[x, y].IsLinked)
                    return _grid[x, y];
        return null;
    }

    public bool CanFit(GameItem item, short x, short y, short count)
    {
        byte w = item.Size.X;
        byte h = item.Size.Y;

        if (x + w > Width || y + h > Height) return false;

        for (int dx = 0; dx < w; dx++)
        {
            for (int dy = 0; dy < h; dy++)
            {
                var slot = _grid[x + dx, y + dy];

                // Anchor cell: allow stacking if same template
                if (dx == 0 && dy == 0)
                {
                    if (!slot.IsEmpty)
                    {
                        if (item.Stackable && slot.ItemTemplateId == item.TemplateId)
                            return slot.ItemCount + count <= slot.MaxCapacity;
                        return false;
                    }
                    continue;
                }

                // Linked cells must be empty
                if (!slot.IsEmpty) return false;
            }
        }

        return true;
    }

    public ItemStatus TryPlace(GameItem item, short x, short y, short count)
    {
        if (!CanFit(item, x, y, count))
            return ItemStatus.NotEnoughSpace;

        byte w = item.Size.X;
        byte h = item.Size.Y;

        var anchor = _grid[x, y];

        // Stacking into existing slot
        if (!anchor.IsEmpty && item.Stackable && anchor.ItemTemplateId == item.TemplateId)
        {
            anchor.ItemCount += count;
            return ItemStatus.Success;
        }

        // Place anchor
        anchor.Set(item.InstanceId, item.TemplateId, count);

        // Link additional cells for multi-cell items
        for (int dx = 0; dx < w; dx++)
        {
            for (int dy = 0; dy < h; dy++)
            {
                if (dx == 0 && dy == 0) continue; // Skip anchor

                var linked = _grid[x + dx, y + dy];
                linked.Set(item.InstanceId, item.TemplateId, 0);
                linked.SlotLink = new IntVector2(x, y); // Points back to anchor
            }
        }

        return ItemStatus.Success;
    }

    public ItemStatus TryPlaceAuto(GameItem item, short count)
    {
        // Try stacking first
        if (item.Stackable)
        {
            for (short y = 0; y < Height; y++)
            {
                for (short x = 0; x < Width; x++)
                {
                    var slot = _grid[x, y];
                    if (!slot.IsEmpty && !slot.IsLinked &&
                        slot.ItemTemplateId == item.TemplateId &&
                        slot.ItemCount + count <= slot.MaxCapacity)
                    {
                        slot.ItemCount += count;
                        return ItemStatus.Success;
                    }
                }
            }
        }

        // Find first position where item fits
        for (short y = 0; y < Height; y++)
        {
            for (short x = 0; x < Width; x++)
            {
                if (CanFit(item, x, y, count))
                    return TryPlace(item, x, y, count);
            }
        }

        return ItemStatus.NotEnoughSpace;
    }

    public ItemStatus Remove(short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return ItemStatus.InvalidSlot;
        if (slot.IsEmpty) return ItemStatus.ItemNotFound;

        // If this is a linked cell, redirect to anchor
        if (slot.IsLinked && slot.SlotLink.HasValue)
        {
            return Remove((short)slot.SlotLink.Value.X, (short)slot.SlotLink.Value.Y, count);
        }

        if (count < slot.ItemCount)
        {
            // Partial removal (stack decrement)
            slot.ItemCount -= count;
            return ItemStatus.Success;
        }

        // Full removal: clear anchor and all linked cells
        var instanceId = slot.ItemInstanceId;
        slot.Clear();

        // Clear linked cells
        for (short cy = 0; cy < Height; cy++)
        {
            for (short cx = 0; cx < Width; cx++)
            {
                var cell = _grid[cx, cy];
                if (cell.IsLinked && cell.SlotLink.HasValue &&
                    cell.SlotLink.Value.X == x && cell.SlotLink.Value.Y == y)
                {
                    cell.Clear();
                }
            }
        }

        return ItemStatus.Success;
    }

    public bool ValidateItem(GameItem item) => true;
}
