/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Lightweight, allocation-free spatial hash grid for per-tick broadphase queries.
/// Built once per tick from the world snapshot, then queried by visibility and collision systems.
///
/// Uses integer cell keys (no tuple allocation). Grid is cleared and rebuilt each tick —
/// all internal buffers are reused across ticks (zero GC pressure in steady state).
/// </summary>
public sealed class SpatialHashGrid
{
    private readonly float _cellSize;
    private readonly float _invCellSize;

    // Cell -> list of object indices in the snapshot
    private readonly Dictionary<long, List<int>> _cells = new();

    // Pool of index lists to avoid allocation
    private readonly List<List<int>> _listPool = new();
    private int _poolIndex;

    public SpatialHashGrid(float cellSize = 500f)
    {
        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
    }

    /// <summary>
    /// Rebuild the grid from a snapshot's objects. Call once per tick.
    /// Only includes IWorldObject3D objects (needs Transform.Position).
    /// </summary>
    public void Build(IReadOnlyList<ThreeD.IWorldObject3D> objects)
    {
        ClearCells();
        for (int i = 0; i < objects.Count; i++)
        {
            var pos = objects[i].Transform.Position;
            var key = CellKey((int)(pos.X * _invCellSize), (int)(pos.Y * _invCellSize));
            if (!_cells.TryGetValue(key, out var list))
            {
                list = RentList();
                _cells[key] = list;
            }
            list.Add(i);
        }
    }

    /// <inheritdoc cref="Build(IReadOnlyList{ThreeD.IWorldObject3D})"/>
    public void Build(IReadOnlyList<ITypelessWorldObject> objects)
    {
        ClearCells();
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not ThreeD.IWorldObject3D obj3d) continue;

            var pos = obj3d.Transform.Position;
            var key = CellKey((int)(pos.X * _invCellSize), (int)(pos.Y * _invCellSize));

            if (!_cells.TryGetValue(key, out var list))
            {
                list = RentList();
                _cells[key] = list;
            }
            list.Add(i);
        }
    }

    private void ClearCells()
    {
        foreach (var kvp in _cells)
        {
            kvp.Value.Clear();
            if (_poolIndex < _listPool.Count)
                _listPool[_poolIndex] = kvp.Value;
            else
                _listPool.Add(kvp.Value);
            _poolIndex++;
        }
        _cells.Clear();
        _poolIndex = 0;
    }

    /// <summary>
    /// Query all object indices within a radius of a position.
    /// Returns indices into the snapshot's AllObjects list.
    /// Uses the provided buffer to avoid allocation.
    /// </summary>
    public void QueryRadius(float x, float y, float radius, List<int> results)
    {
        results.Clear();
        int minCx = (int)((x - radius) * _invCellSize);
        int maxCx = (int)((x + radius) * _invCellSize);
        int minCy = (int)((y - radius) * _invCellSize);
        int maxCy = (int)((y + radius) * _invCellSize);

        for (int cx = minCx; cx <= maxCx; cx++)
        {
            for (int cy = minCy; cy <= maxCy; cy++)
            {
                var key = CellKey(cx, cy);
                if (_cells.TryGetValue(key, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                        results.Add(list[i]);
                }
            }
        }
    }

    private static long CellKey(int cx, int cy)
        => ((long)cx << 32) | (uint)cy;

    private List<int> RentList()
    {
        if (_poolIndex < _listPool.Count)
        {
            var list = _listPool[_poolIndex++];
            list.Clear();
            return list;
        }
        _poolIndex++;
        var newList = new List<int>(8);
        _listPool.Add(newList);
        return newList;
    }
}
