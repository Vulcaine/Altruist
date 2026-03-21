/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Cell attribute flags for walkability grids.
/// Games can define additional flags beyond the base set.
/// </summary>
[Flags]
public enum CellAttribute : byte
{
    None = 0,
    Blocked = 0x01,
    Water = 0x02,
    Banpk = 0x04,
}

/// <summary>
/// A 2D walkability grid for a zone/map. Each cell stores attribute flags
/// indicating whether the cell is walkable, blocked, water, etc.
///
/// Game coordinates are converted to cell indices using CellScale
/// (e.g., cellX = gameX / CellScale).
/// </summary>
public interface IWalkabilityGrid
{
    int Width { get; }
    int Height { get; }
    int CellScale { get; }

    /// <summary>Get the raw attribute byte at a cell coordinate.</summary>
    byte GetCell(int cellX, int cellY);

    /// <summary>Check if a cell is walkable (not blocked).</summary>
    bool IsWalkable(int cellX, int cellY);

    /// <summary>Check if a game-world position is walkable.</summary>
    bool IsPositionWalkable(float worldX, float worldY);

    /// <summary>Check if movement from one position to another is valid (line-of-walk).</summary>
    bool CanMoveTo(float fromX, float fromY, float toX, float toY);
}

/// <summary>
/// Concrete walkability grid backed by a byte array.
/// The game layer provides the raw data (parsed from whatever format).
/// </summary>
public sealed class WalkabilityGrid : IWalkabilityGrid
{
    private readonly byte[] _cells;

    public int Width { get; }
    public int Height { get; }
    public int CellScale { get; }

    public WalkabilityGrid(int width, int height, int cellScale, byte[] cells)
    {
        if (cells.Length < width * height)
            throw new ArgumentException($"Cell data too small: {cells.Length} < {width * height}");

        Width = width;
        Height = height;
        CellScale = cellScale;
        _cells = cells;
    }

    public byte GetCell(int cellX, int cellY)
    {
        if (cellX < 0 || cellX >= Width || cellY < 0 || cellY >= Height)
            return (byte)CellAttribute.Blocked;
        return _cells[cellY * Width + cellX];
    }

    public bool IsWalkable(int cellX, int cellY)
    {
        return (GetCell(cellX, cellY) & (byte)CellAttribute.Blocked) == 0;
    }

    public bool IsPositionWalkable(float worldX, float worldY)
    {
        int cellX = (int)(worldX / CellScale);
        int cellY = (int)(worldY / CellScale);
        return IsWalkable(cellX, cellY);
    }

    public bool CanMoveTo(float fromX, float fromY, float toX, float toY)
    {
        // Simple Bresenham line check — every cell along the path must be walkable
        int x0 = (int)(fromX / CellScale);
        int y0 = (int)(fromY / CellScale);
        int x1 = (int)(toX / CellScale);
        int y1 = (int)(toY / CellScale);

        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (!IsWalkable(x0, y0)) return false;
            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }

        return true;
    }
}

/// <summary>
/// Manages walkability grids per zone. Games register grids when zones activate.
/// </summary>
public interface IWalkabilityService
{
    void RegisterGrid(string zoneName, IWalkabilityGrid grid);
    void UnregisterGrid(string zoneName);
    IWalkabilityGrid? GetGrid(string zoneName);
    bool IsPositionWalkable(string zoneName, float worldX, float worldY);
    bool CanMoveTo(string zoneName, float fromX, float fromY, float toX, float toY);
}

[Service(typeof(IWalkabilityService))]
[ConditionalOnConfig("altruist:game")]
public sealed class WalkabilityService : IWalkabilityService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IWalkabilityGrid> _grids = new();

    public void RegisterGrid(string zoneName, IWalkabilityGrid grid) => _grids[zoneName] = grid;
    public void UnregisterGrid(string zoneName) => _grids.TryRemove(zoneName, out _);
    public IWalkabilityGrid? GetGrid(string zoneName) => _grids.GetValueOrDefault(zoneName);

    public bool IsPositionWalkable(string zoneName, float worldX, float worldY)
    {
        var grid = GetGrid(zoneName);
        return grid?.IsPositionWalkable(worldX, worldY) ?? true;
    }

    public bool CanMoveTo(string zoneName, float fromX, float fromY, float toX, float toY)
    {
        var grid = GetGrid(zoneName);
        return grid?.CanMoveTo(fromX, fromY, toX, toY) ?? true;
    }
}
