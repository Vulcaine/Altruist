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
/// <remarks>
/// <para>
/// When to use: inventories where item footprint matters (<see cref="GameItem.Size"/>). For simple 1x1 stack bags use
/// <see cref="SlotStorage"/>; for named equipment slots use <see cref="EquipmentStorage"/>.
/// Usually created by <see cref="IInventoryService.CreateContainer"/> with <see cref="ContainerType.Grid"/>.
/// </para>
/// <para>
/// Coordinates: X = column (0..Width-1), Y = row (0..Height-1), (0,0) top-left; an item placed at (x,y) covers
/// x..x+Size.X-1 and y..y+Size.Y-1. The anchor cell holds the instance id and count; the other covered cells hold the
/// same id, count 0 and <see cref="StorageSlot.SlotLink"/> pointing at the anchor. Auto placement scans row by row
/// (Y outer, X inner). Stacking only happens on the anchor cell of a same-template stackable item, capped at the slot capacity.
/// </para>
/// </remarks>
public class GridStorage : IInventoryContainer
{
    private readonly StorageSlot[,] _grid;

    /// <inheritdoc/>
    public string ContainerId { get; }
    /// <inheritdoc/>
    public string OwnerId { get; }
    /// <summary>Always <see cref="ContainerType.Grid"/>.</summary>
    public ContainerType ContainerType => ContainerType.Grid;
    /// <summary>Number of columns.</summary>
    public short Width { get; }
    /// <summary>Number of rows.</summary>
    public short Height { get; }
    /// <summary>Maximum stack size of each cell.</summary>
    public short SlotCapacity { get; }

    /// <summary>Creates an empty grid. Prefer <see cref="IInventoryService.CreateContainer"/> so the service can find it.</summary>
    /// <param name="containerId">Container id.</param>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="slotCapacity">Maximum stack size per cell.</param>
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

    /// <inheritdoc/>
    /// <remarks>May return a linked cell; follow <see cref="StorageSlot.SlotLink"/> to reach the anchor.</remarks>
    public StorageSlot? GetSlot(short x, short y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return null;
        return _grid[x, y];
    }

    /// <inheritdoc/>
    /// <remarks>Row-major order (Y outer, X inner).</remarks>
    public IReadOnlyCollection<StorageSlot> GetAllSlots()
    {
        var list = new List<StorageSlot>(Width * Height);
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                list.Add(_grid[x, y]);
        return list;
    }

    /// <inheritdoc/>
    /// <remarks>Row-major order; includes linked cells (check <see cref="StorageSlot.IsLinked"/> to list each item once).</remarks>
    public IEnumerable<StorageSlot> GetOccupiedSlots()
    {
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                if (!_grid[x, y].IsEmpty)
                    yield return _grid[x, y];
    }

    /// <inheritdoc/>
    public StorageSlot? FindItemSlot(string itemInstanceId)
    {
        for (short y = 0; y < Height; y++)
            for (short x = 0; x < Width; x++)
                if (_grid[x, y].ItemInstanceId == itemInstanceId && !_grid[x, y].IsLinked)
                    return _grid[x, y];
        return null;
    }

    /// <summary>
    /// True when the item's footprint at (x,y) stays inside the grid and every covered cell is empty, or when the anchor
    /// cell already holds a same-template stackable item with room for <paramref name="count"/> more.
    /// </summary>
    /// <param name="item">Item to test (uses <see cref="GameItem.Size"/>).</param>
    /// <param name="x">Anchor column; must be non-negative.</param>
    /// <param name="y">Anchor row; must be non-negative.</param>
    /// <param name="count">Stack count to test.</param>
    /// <returns>True when <see cref="TryPlace"/> would succeed.</returns>
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

    /// <summary>Places the item with its anchor at (x,y), linking the other covered cells, or stacks onto a matching anchor.</summary>
    /// <param name="item">Item to place.</param>
    /// <param name="x">Anchor column; must be non-negative.</param>
    /// <param name="y">Anchor row; must be non-negative.</param>
    /// <param name="count">Stack count to place.</param>
    /// <returns><see cref="ItemStatus.Success"/> or <see cref="ItemStatus.NotEnoughSpace"/>.</returns>
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

    /// <inheritdoc/>
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

    /// <summary>
    /// Removes items from the stack at (x,y); a linked cell redirects to its anchor. When the whole stack is removed the
    /// anchor and all its linked cells are cleared.
    /// </summary>
    /// <param name="x">Column (anchor or linked cell).</param>
    /// <param name="y">Row (anchor or linked cell).</param>
    /// <param name="count">Number of items to remove.</param>
    /// <returns><see cref="ItemStatus.Success"/>, <see cref="ItemStatus.InvalidSlot"/> or <see cref="ItemStatus.ItemNotFound"/>.</returns>
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

    /// <summary>Accepts every item.</summary>
    /// <param name="item">Item to test.</param>
    /// <returns>Always true.</returns>
    public bool ValidateItem(GameItem item) => true;
}
