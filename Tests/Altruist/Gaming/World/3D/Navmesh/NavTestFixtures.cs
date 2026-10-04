using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;

namespace Tests.Gaming.World.Navmesh;

/// <summary>Helpers for building controlled walkability grids and terrain
/// providers without spinning up the whole world. Every nav-mesh test
/// composes one of these to get a known input → output mapping.</summary>
internal static class NavTestFixtures
{
    /// <summary>Build a walkability grid from an ASCII map. '.' = walkable,
    /// '#' = blocked, 'w' = water (also blocked by default). The first row
    /// in the array is grid Y=0 (top of the map). Cell scale defaults to 2
    /// to mirror Metin2's server_attr layout.</summary>
    public static IWalkabilityGrid GridFromAscii(string[] rows, int cellScale = 2, float baseX = 0, float baseY = 0)
    {
        if (rows == null || rows.Length == 0)
            throw new ArgumentException("Need at least one row.", nameof(rows));

        int width = rows[0].Length;
        int height = rows.Length;
        var cells = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            if (rows[y].Length != width)
                throw new ArgumentException($"Row {y} has length {rows[y].Length}, expected {width}.");

            for (int x = 0; x < width; x++)
            {
                cells[y * width + x] = rows[y][x] switch
                {
                    '#' => (byte)CellAttribute.Blocked,
                    'w' or 'W' => (byte)CellAttribute.Water,
                    '.' or ' ' => 0,
                    _ => throw new ArgumentException($"Unknown cell '{rows[y][x]}' at ({x},{y}).")
                };
            }
        }

        return new WalkabilityGrid(width, height, cellScale, cells, baseX, baseY);
    }

    /// <summary>Flat terrain at a constant Y. The simplest possible
    /// <see cref="ITerrainProvider"/> — handy for testing topology in
    /// isolation from elevation behavior.</summary>
    public static ITerrainProvider FlatTerrain(float y) => new FlatTerrainProvider(y);

    /// <summary>Linear ramp along +X: y = baseY + slope·x. Useful for testing
    /// the greedy-mesher's MaxIntraRectHeightDelta filter.</summary>
    public static ITerrainProvider RampTerrain(float baseY, float slopePerUnitX)
        => new RampTerrainProvider(baseY, slopePerUnitX);

    /// <summary>Hand-craft a NavMeshGraph from raw polygons + edge data so
    /// pathfinder/funnel tests don't depend on the builder's correctness.
    /// Each polygon is given as 4 (x,z) corners CCW; Y=0 throughout.</summary>
    public static NavMeshGraph SyntheticGraph(
        IReadOnlyList<(float x, float z)[]> polys,
        IReadOnlyList<(int from, int to, Vector3 v0, Vector3 v1)> edges)
    {
        var navPolys = new NavPoly[polys.Count];
        var min = new Vector3(float.PositiveInfinity, 0f, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, 0f, float.NegativeInfinity);
        for (int i = 0; i < polys.Count; i++)
        {
            var verts = polys[i].Select(p => new Vector3(p.x, 0f, p.z)).ToArray();
            navPolys[i] = new NavPoly(i, verts);
            foreach (var v in verts)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        // Sort edges by FromPoly so CSR layout is contiguous per poly.
        var grouped = edges.GroupBy(e => e.from).OrderBy(g => g.Key).ToList();
        var offsets = new int[polys.Count + 1];
        var allEdges = new List<NavEdge>(edges.Count);
        for (int i = 0; i < polys.Count; i++)
        {
            offsets[i] = allEdges.Count;
            var match = grouped.FirstOrDefault(g => g.Key == i);
            if (match != null)
            {
                foreach (var e in match)
                {
                    float cost = Vector3.Distance(navPolys[e.from].Centroid, navPolys[e.to].Centroid);
                    allEdges.Add(new NavEdge(e.from, e.to, e.v0, e.v1, cost));
                }
            }
        }
        offsets[polys.Count] = allEdges.Count;

        // NavMeshGraph constructor is internal — same assembly via InternalsVisibleTo
        // would be cleanest, but the test project doesn't have that wired up. Use
        // reflection here; this is the only place tests need to bypass visibility.
        var ctor = typeof(NavMeshGraph).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (NavMeshGraph)ctor.Invoke(new object[] { navPolys, allEdges.ToArray(), offsets, min, max });
    }

    private sealed class FlatTerrainProvider : ITerrainProvider
    {
        private readonly float _y;
        public FlatTerrainProvider(float y) => _y = y;
        public bool IsWalkable(float x, float y, float z) => true;
        public float GetHeight(float x, float z) => _y;
    }

    private sealed class RampTerrainProvider : ITerrainProvider
    {
        private readonly float _baseY;
        private readonly float _slope;
        public RampTerrainProvider(float baseY, float slope) { _baseY = baseY; _slope = slope; }
        public bool IsWalkable(float x, float y, float z) => true;
        public float GetHeight(float x, float z) => _baseY + _slope * x;
    }
}
