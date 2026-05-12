using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshBuilderTests
{
    [Fact]
    public void Build_AllBlocked_ReturnsNull()
    {
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "###",
            "###",
            "###",
        });
        NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0)).Should().BeNull();
    }

    [Fact]
    public void Build_AllWalkable_ProducesExactlyOneRectangle()
    {
        // 3×3 walkable cells should greedy-mesh into a single 3×3 rectangle —
        // nothing to merge against, no internal seams.
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "...",
            "...",
            "...",
        });

        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        mesh.PolyCount.Should().Be(1);
        mesh.GetPoly(0).Vertices.Should().HaveCount(4);
        mesh.GetOutgoingEdges(0).Length.Should().Be(0); // no neighbors
    }

    [Fact]
    public void Build_TwoRowsBlockedBetween_ProducesTwoDisconnectedRectangles()
    {
        // ...
        // ###   <-- blocking row separates top and bottom
        // ...
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "...",
            "###",
            "...",
        });

        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        mesh.PolyCount.Should().Be(2);
        // No portals between them — A* would (correctly) fail to find a path.
        mesh.GetOutgoingEdges(0).Length.Should().Be(0);
        mesh.GetOutgoingEdges(1).Length.Should().Be(0);
    }

    [Fact]
    public void Build_AdjacentRectangles_ConnectedByPortalsBothDirections()
    {
        // Greedy-mesher tends to extend rows downward greedily; we force two
        // distinct rectangles by varying terrain Y past MaxIntraRectHeightDelta
        // between rows. After that there must be a portal in BOTH directions
        // between the two rects (NavEdge is directed).
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "...",
            "...",
        });
        var ramp = NavTestFixtures.RampTerrain(baseY: 0, slopePerUnitX: 0); // X is constant
        // Use a custom builder option that forces row-split via a tighter height tolerance
        var opts = new NavMeshBuilder.Options { MaxIntraRectHeightDelta = 0.01f };
        // Provide a Z-varying terrain so rows differ by ~10m: y = z
        var zTerrain = new ZRampTerrain();
        var mesh = NavMeshBuilder.Build(grid, zTerrain, opts)!;

        mesh.PolyCount.Should().Be(2);

        // Find each direction once.
        var fromZeroToOne = false;
        var fromOneToZero = false;
        foreach (var e in mesh.GetOutgoingEdges(0)) if (e.ToPoly == 1) fromZeroToOne = true;
        foreach (var e in mesh.GetOutgoingEdges(1)) if (e.ToPoly == 0) fromOneToZero = true;

        fromZeroToOne.Should().BeTrue("rect 0 must portal into rect 1");
        fromOneToZero.Should().BeTrue("rect 1 must portal back into rect 0");
    }

    [Fact]
    public void Build_PortalEdgeOrientation_V0IsLeftOfCrossingDirection()
    {
        // Two stacked rectangles. Crossing FROM the +Z (south) rect TO the
        // -Z (north) rect means walking in -Z. "Left" of -Z is +X. So V0
        // (the LEFT endpoint per our invariant) must have higher X than V1.
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "..",
            "..",
        });
        var opts = new NavMeshBuilder.Options { MaxIntraRectHeightDelta = 0.01f };
        var mesh = NavMeshBuilder.Build(grid, new ZRampTerrain(), opts)!;
        mesh.PolyCount.Should().Be(2);

        // Identify which poly is "north" (smaller Z centroid).
        int north = mesh.GetPoly(0).Centroid.Z < mesh.GetPoly(1).Centroid.Z ? 0 : 1;
        int south = north == 0 ? 1 : 0;

        var southToNorth = mesh.GetOutgoingEdges(south).ToArray()
            .First(e => e.ToPoly == north);

        // Crossing south → north walks in -Z, so V0 (left) should be at higher X.
        southToNorth.V0.X.Should().BeGreaterThan(southToNorth.V1.X);
        // Both endpoints share the same Z (the boundary line).
        southToNorth.V0.Z.Should().BeApproximately(southToNorth.V1.Z, 0.001f);
    }

    [Fact]
    public void Build_TerrainHeight_IsBakedIntoVertices()
    {
        // Single-cell rectangle at world origin with a flat terrain at Y=12.
        // Every vertex should land at Y=12.
        var grid = NavTestFixtures.GridFromAscii(new[] { "." });
        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(12f))!;
        mesh.PolyCount.Should().Be(1);
        foreach (var v in mesh.GetPoly(0).Vertices)
            v.Y.Should().BeApproximately(12f, 0.001f);
    }

    [Fact]
    public void Build_GreedyMeshing_CompressesLargeOpenAreas()
    {
        // 8×8 fully walkable + flat terrain → must collapse to a single rect,
        // proving the greedy-mesher actually merges. (Without merging this
        // would be 64 separate polys.)
        var rows = new string[8];
        for (int i = 0; i < 8; i++) rows[i] = new string('.', 8);
        var grid = NavTestFixtures.GridFromAscii(rows);

        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        mesh.PolyCount.Should().Be(1);
        mesh.GetPoly(0).MaxXZ.X.Should().BeApproximately(grid.Width * grid.CellScale, 0.001f);
        mesh.GetPoly(0).MaxXZ.Y.Should().BeApproximately(grid.Height * grid.CellScale, 0.001f);
    }

    [Fact]
    public void Build_ObstacleInMiddle_ProducesCMeshAroundIt()
    {
        // .....
        // .....
        // ..#..   ← single blocker
        // .....
        // .....
        // The greedy-mesher can't merge across the blocker, so we expect
        // multiple rectangles, all reachable from each other (A* should
        // route around the obstacle).
        var grid = NavTestFixtures.GridFromAscii(new[] {
            ".....",
            ".....",
            "..#..",
            ".....",
            ".....",
        });

        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        mesh.PolyCount.Should().BeGreaterThan(1);

        // Every poly should be reachable from poly 0 — a BFS over outgoing
        // edges must visit every index.
        var seen = new HashSet<int> { 0 };
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            foreach (var e in mesh.GetOutgoingEdges(cur).ToArray())
                if (e.ToPoly >= 0 && seen.Add(e.ToPoly))
                    queue.Enqueue(e.ToPoly);
        }
        seen.Count.Should().Be(mesh.PolyCount);
    }

    [Fact]
    public void Build_WaterIsBlockedByDefault()
    {
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "...",
            "wWw",
            "...",
        });
        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        // Water row is non-walkable → top and bottom rows produce 2 disconnected polys.
        mesh.PolyCount.Should().Be(2);
        mesh.GetOutgoingEdges(0).Length.Should().Be(0);
    }

    [Fact]
    public void Build_HeightDeltaTooLarge_PreventsMerging()
    {
        // Single row, two cells. Slope of 1.0 per unit means the second cell
        // is 2m higher than the first (cellScale=2). With tolerance < 2 the
        // mesher can't fold them into one rect.
        var grid = NavTestFixtures.GridFromAscii(new[] { ".." });
        var ramp = NavTestFixtures.RampTerrain(baseY: 0, slopePerUnitX: 1f);
        var opts = new NavMeshBuilder.Options { MaxIntraRectHeightDelta = 0.5f };

        var mesh = NavMeshBuilder.Build(grid, ramp, opts)!;
        mesh.PolyCount.Should().Be(2);
    }

    /// <summary>Terrain whose Y varies linearly with Z (north–south).</summary>
    private sealed class ZRampTerrain : ITerrainProvider
    {
        public bool IsWalkable(float x, float y, float z) => true;
        public float GetHeight(float x, float z) => z;
    }
}
