/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>The compiled nav-mesh: an immutable polygon graph with adjacency
/// and a 2D spatial index for point→polygon lookup. Built once per zone by
/// <see cref="NavMeshBuilder"/> and consumed by the pathfinder, funnel, and
/// services. All queries are read-only and thread-safe.</summary>
/// <remarks>Register it per zone with <see cref="INavMeshService.RegisterMesh"/>; most callers query through the service rather than the graph.</remarks>
public sealed class NavMeshGraph
{
    private readonly NavPoly[] _polys;
    /// <summary>Edges keyed by index. Each polygon owns the edges where
    /// <c>FromPoly == polyIdx</c>; the same edge appears reversed under
    /// <c>ToPoly</c>. <see cref="GetOutgoingEdges"/> hides this.</summary>
    private readonly NavEdge[] _edges;
    private readonly int[] _edgeOffsetsByPoly; // _edges[_edgeOffsetsByPoly[i] .. _edgeOffsetsByPoly[i+1]) = poly i's outgoing
    private readonly NavSpatialIndex _spatialIndex;

    /// <summary>Minimum corner of all polygon vertices.</summary>
    public Vector3 BoundsMin { get; }
    /// <summary>Maximum corner of all polygon vertices.</summary>
    public Vector3 BoundsMax { get; }
    /// <summary>Number of polygons.</summary>
    public int PolyCount => _polys.Length;
    /// <summary>Number of directed portal edges (each adjacency appears once per direction).</summary>
    public int EdgeCount => _edges.Length;

    internal NavMeshGraph(
        NavPoly[] polys,
        NavEdge[] edges,
        int[] edgeOffsetsByPoly,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        _polys = polys;
        _edges = edges;
        _edgeOffsetsByPoly = edgeOffsetsByPoly;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        _spatialIndex = new NavSpatialIndex(polys, boundsMin, boundsMax);
    }

    /// <summary>Returns the polygon at <paramref name="polyIdx"/>.</summary>
    /// <param name="polyIdx">Polygon index in [0, <see cref="PolyCount"/>).</param>
    /// <exception cref="IndexOutOfRangeException">Index out of range.</exception>
    public NavPoly GetPoly(int polyIdx) => _polys[polyIdx];

    /// <summary>Portal edges leaving <paramref name="polyIdx"/> (allocation-free view).</summary>
    /// <param name="polyIdx">Polygon index.</param>
    public ReadOnlySpan<NavEdge> GetOutgoingEdges(int polyIdx)
    {
        int start = _edgeOffsetsByPoly[polyIdx];
        int end = _edgeOffsetsByPoly[polyIdx + 1];
        return new ReadOnlySpan<NavEdge>(_edges, start, end - start);
    }

    /// <summary>Locate the polygon containing the given XZ point. Returns
    /// false if the point is outside every polygon's footprint.</summary>
    /// <param name="point">World point (Y ignored).</param>
    /// <param name="polyIdx">Containing polygon index, or -1.</param>
    public bool TryLocate(Vector3 point, out int polyIdx)
        => _spatialIndex.TryLocate(point.X, point.Z, out polyIdx);

    /// <summary>Find the polygon containing the given point, or — if none
    /// contains it — the polygon whose XZ footprint has the closest point
    /// within <paramref name="maxDistance"/>. Returns the on-mesh position
    /// in <paramref name="onMesh"/> with Y from the polygon.</summary>
    /// <param name="point">World point (Y ignored).</param>
    /// <param name="maxDistance">Max XZ snap distance in world units.</param>
    /// <param name="polyIdx">Chosen polygon index, or -1.</param>
    /// <param name="onMesh">Snapped point, or <paramref name="point"/> on failure.</param>
    public bool TrySamplePosition(Vector3 point, float maxDistance, out int polyIdx, out Vector3 onMesh)
    {
        if (TryLocate(point, out polyIdx))
        {
            var poly = _polys[polyIdx];
            onMesh = new Vector3(point.X, poly.SampleYAt(point.X, point.Z), point.Z);
            return true;
        }

        // Brute-force nearest-edge search on candidates the spatial index
        // suggests. Maps with a few thousand polys this is a microsecond.
        polyIdx = -1;
        onMesh = point;
        float bestSq = maxDistance * maxDistance;

        foreach (var idx in _spatialIndex.QueryRadius(point.X, point.Z, maxDistance))
        {
            var poly = _polys[idx];
            var nearest = NearestPointOnPolyXZ(poly, point.X, point.Z);
            float dx = nearest.X - point.X;
            float dz = nearest.Z - point.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq < bestSq)
            {
                bestSq = distSq;
                polyIdx = idx;
                onMesh = nearest;
            }
        }
        return polyIdx >= 0;
    }

    /// <summary>Resolve the portal edge between two adjacent polygons.
    /// Returns false if they don't share an edge in the graph.</summary>
    /// <param name="fromPoly">Polygon being left.</param>
    /// <param name="toPoly">Polygon being entered.</param>
    /// <param name="portal">The edge, oriented from <paramref name="fromPoly"/> to <paramref name="toPoly"/>.</param>
    public bool TryGetPortal(int fromPoly, int toPoly, out NavEdge portal)
    {
        foreach (var edge in GetOutgoingEdges(fromPoly))
        {
            if (edge.ToPoly == toPoly)
            {
                portal = edge;
                return true;
            }
        }
        portal = default;
        return false;
    }

    private static Vector3 NearestPointOnPolyXZ(NavPoly poly, float x, float z)
    {
        // Walk every edge segment, track the closest projected point. For
        // convex polys this is exact.
        var query = new Vector2(x, z);
        Vector3 best = poly.Vertices[0];
        float bestSq = float.PositiveInfinity;

        int n = poly.Vertices.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly.Vertices[j];
            var b = poly.Vertices[i];
            var ab = new Vector2(b.X - a.X, b.Z - a.Z);
            var ap = new Vector2(x - a.X, z - a.Z);
            float lenSq = ab.LengthSquared();
            float t = lenSq > 1e-8f
                ? Math.Clamp(Vector2.Dot(ap, ab) / lenSq, 0f, 1f)
                : 0f;
            var proj = new Vector2(a.X + ab.X * t, a.Z + ab.Y * t);
            float distSq = (proj - query).LengthSquared();
            if (distSq < bestSq)
            {
                bestSq = distSq;
                float y = a.Y + (b.Y - a.Y) * t;
                best = new Vector3(proj.X, y, proj.Y);
            }
        }
        return best;
    }
}

/// <summary>Uniform-grid spatial index for point→polygon lookup. Each cell
/// stores the indices of polys whose AABB overlaps it; lookup is O(1) for
/// the cell hash plus O(k) over candidate polys (typically 1–4).</summary>
internal sealed class NavSpatialIndex
{
    private readonly int _cellsX;
    private readonly int _cellsZ;
    private readonly float _cellSize;
    private readonly float _originX;
    private readonly float _originZ;
    private readonly int[][] _cells;
    private readonly NavPoly[] _polys;

    public NavSpatialIndex(NavPoly[] polys, Vector3 boundsMin, Vector3 boundsMax)
    {
        _polys = polys;
        // Aim for ~1 poly per cell average. Cell size scales with map area
        // and poly count so the grid stays roughly square in memory.
        float worldW = MathF.Max(1f, boundsMax.X - boundsMin.X);
        float worldD = MathF.Max(1f, boundsMax.Z - boundsMin.Z);
        float area = worldW * worldD;
        float targetCellArea = MathF.Max(4f, area / Math.Max(1, polys.Length));
        _cellSize = MathF.Max(2f, MathF.Sqrt(targetCellArea));

        _originX = boundsMin.X;
        _originZ = boundsMin.Z;
        _cellsX = Math.Max(1, (int)MathF.Ceiling(worldW / _cellSize));
        _cellsZ = Math.Max(1, (int)MathF.Ceiling(worldD / _cellSize));

        var buckets = new List<int>[_cellsX * _cellsZ];
        for (int p = 0; p < polys.Length; p++)
        {
            var poly = polys[p];
            int x0 = Math.Clamp((int)((poly.MinXZ.X - _originX) / _cellSize), 0, _cellsX - 1);
            int x1 = Math.Clamp((int)((poly.MaxXZ.X - _originX) / _cellSize), 0, _cellsX - 1);
            int z0 = Math.Clamp((int)((poly.MinXZ.Y - _originZ) / _cellSize), 0, _cellsZ - 1);
            int z1 = Math.Clamp((int)((poly.MaxXZ.Y - _originZ) / _cellSize), 0, _cellsZ - 1);

            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                int cellIdx = z * _cellsX + x;
                (buckets[cellIdx] ??= new List<int>(2)).Add(p);
            }
        }

        _cells = new int[buckets.Length][];
        for (int i = 0; i < buckets.Length; i++)
            _cells[i] = buckets[i]?.ToArray() ?? Array.Empty<int>();
    }

    public bool TryLocate(float x, float z, out int polyIdx)
    {
        int cx = (int)((x - _originX) / _cellSize);
        int cz = (int)((z - _originZ) / _cellSize);
        if (cx < 0 || cx >= _cellsX || cz < 0 || cz >= _cellsZ)
        {
            polyIdx = -1;
            return false;
        }

        foreach (int p in _cells[cz * _cellsX + cx])
        {
            if (_polys[p].ContainsXZ(x, z))
            {
                polyIdx = p;
                return true;
            }
        }
        polyIdx = -1;
        return false;
    }

    public IEnumerable<int> QueryRadius(float x, float z, float radius)
    {
        int x0 = Math.Clamp((int)((x - radius - _originX) / _cellSize), 0, _cellsX - 1);
        int x1 = Math.Clamp((int)((x + radius - _originX) / _cellSize), 0, _cellsX - 1);
        int z0 = Math.Clamp((int)((z - radius - _originZ) / _cellSize), 0, _cellsZ - 1);
        int z1 = Math.Clamp((int)((z + radius - _originZ) / _cellSize), 0, _cellsZ - 1);

        var seen = new HashSet<int>();
        for (int cz = z0; cz <= z1; cz++)
        for (int cx = x0; cx <= x1; cx++)
        {
            foreach (int p in _cells[cz * _cellsX + cx])
                if (seen.Add(p))
                    yield return p;
        }
    }
}
