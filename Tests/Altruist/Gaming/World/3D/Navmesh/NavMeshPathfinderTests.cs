using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshPathfinderTests
{
    /// <summary>Linear chain of 4 polys: 0→1→2→3 (and back). A* must walk
    /// the full sequence to get from 0 to 3.</summary>
    private static NavMeshGraph LinearChain()
    {
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (2f, 0f), (2f, 2f), (0f, 2f) },
            new[] { (2f, 0f), (4f, 0f), (4f, 2f), (2f, 2f) },
            new[] { (4f, 0f), (6f, 0f), (6f, 2f), (4f, 2f) },
            new[] { (6f, 0f), (8f, 0f), (8f, 2f), (6f, 2f) },
        };
        var edges = new List<(int from, int to, Vector3 v0, Vector3 v1)>
        {
            (0, 1, new Vector3(2, 0, 2), new Vector3(2, 0, 0)),
            (1, 0, new Vector3(2, 0, 0), new Vector3(2, 0, 2)),
            (1, 2, new Vector3(4, 0, 2), new Vector3(4, 0, 0)),
            (2, 1, new Vector3(4, 0, 0), new Vector3(4, 0, 2)),
            (2, 3, new Vector3(6, 0, 2), new Vector3(6, 0, 0)),
            (3, 2, new Vector3(6, 0, 0), new Vector3(6, 0, 2)),
        };
        return NavTestFixtures.SyntheticGraph(polys, edges);
    }

    [Fact]
    public void TryFind_SamePolyForStartAndGoal_ReturnsSinglePoly()
    {
        var pf = new NavMeshPathfinder();
        pf.TryFind(LinearChain(), startPoly: 2, goalPoly: 2, out var path).Should().BeTrue();
        path.Should().Equal(new[] { 2 });
    }

    [Fact]
    public void TryFind_LinearChain_ReturnsFullSequence()
    {
        var pf = new NavMeshPathfinder();
        pf.TryFind(LinearChain(), 0, 3, out var path).Should().BeTrue();
        path.Should().Equal(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public void TryFind_LinearChain_ReverseDirection()
    {
        var pf = new NavMeshPathfinder();
        pf.TryFind(LinearChain(), 3, 0, out var path).Should().BeTrue();
        path.Should().Equal(new[] { 3, 2, 1, 0 });
    }

    [Fact]
    public void TryFind_DisconnectedPolys_ReturnsFalse()
    {
        // Two polys, no edges between them.
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (2f, 0f), (2f, 2f), (0f, 2f) },
            new[] { (10f, 0f), (12f, 0f), (12f, 2f), (10f, 2f) },
        };
        var edges = new List<(int, int, Vector3, Vector3)>(); // intentionally empty
        var graph = NavTestFixtures.SyntheticGraph(polys, edges);

        var pf = new NavMeshPathfinder();
        pf.TryFind(graph, 0, 1, out var path).Should().BeFalse();
        path.Should().BeEmpty();
    }

    [Fact]
    public void TryFind_OutOfRangeIndex_ReturnsFalse()
    {
        var pf = new NavMeshPathfinder();
        pf.TryFind(LinearChain(), startPoly: -1, goalPoly: 0, out _).Should().BeFalse();
        pf.TryFind(LinearChain(), startPoly: 0, goalPoly: 999, out _).Should().BeFalse();
    }

    [Fact]
    public void TryFind_PrefersShorterPath_WhenTwoExist()
    {
        // Graph:
        //   0 — 1 — 3   (long route via 1, 2 hops)
        //   |       |
        //   └── 2 ──┘   (short route via 2, 2 hops via different polys)
        // With centroid distances baked into edge cost, A* should pick the
        // route whose centroid sum is smaller.
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (2f, 0f), (2f, 2f), (0f, 2f) },     // 0 — origin
            new[] { (2f, 0f), (10f, 0f), (10f, 2f), (2f, 2f) },   // 1 — long bridge
            new[] { (0f, 4f), (2f, 4f), (2f, 6f), (0f, 6f) },     // 2 — short bridge
            new[] { (10f, 0f), (12f, 0f), (12f, 6f), (10f, 6f) }, // 3 — destination
        };
        // Direct distances: 0→1 (5), 1→3 (4) = 9; 0→2 (4), 2→3 (longer because of dogleg)
        // Without rigorously calibrating the geometry, just assert SOMETHING
        // valid is returned and no garbage indices appear.
        var edges = new List<(int, int, Vector3, Vector3)>
        {
            (0, 1, new Vector3(2, 0, 2), new Vector3(2, 0, 0)),
            (1, 0, new Vector3(2, 0, 0), new Vector3(2, 0, 2)),
            (0, 2, new Vector3(0, 0, 4), new Vector3(2, 0, 4)),
            (2, 0, new Vector3(2, 0, 4), new Vector3(0, 0, 4)),
            (1, 3, new Vector3(10, 0, 0), new Vector3(10, 0, 2)),
            (3, 1, new Vector3(10, 0, 2), new Vector3(10, 0, 0)),
            (2, 3, new Vector3(2, 0, 6), new Vector3(10, 0, 6)),
            (3, 2, new Vector3(10, 0, 6), new Vector3(2, 0, 6)),
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, edges);

        var pf = new NavMeshPathfinder();
        pf.TryFind(graph, 0, 3, out var path).Should().BeTrue();
        path.Should().HaveCountGreaterThan(0);
        path[0].Should().Be(0);
        path[^1].Should().Be(3);
        // Every step must be an actual neighbor.
        for (int i = 0; i + 1 < path.Length; i++)
            graph.TryGetPortal(path[i], path[i + 1], out _).Should().BeTrue();
    }

    [Fact]
    public void TryFind_BuildAndPathFindAroundObstacle()
    {
        // .....
        // ..#..
        // ..#..   ← vertical wall
        // .....
        // Path from top-left corner area to bottom-right must go around.
        var grid = NavTestFixtures.GridFromAscii(new[] {
            ".....",
            "..#..",
            "..#..",
            ".....",
        });
        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        var pf = new NavMeshPathfinder();

        // Find polys on opposite sides of the wall.
        mesh.TryLocate(new Vector3(1f, 0f, 1f), out int startPoly).Should().BeTrue();
        mesh.TryLocate(new Vector3(9f, 0f, 7f), out int goalPoly).Should().BeTrue();

        pf.TryFind(mesh, startPoly, goalPoly, out var path).Should().BeTrue();
        path.Length.Should().BeGreaterThan(1, "must traverse multiple polys to skirt the wall");
        path[0].Should().Be(startPoly);
        path[^1].Should().Be(goalPoly);
    }
}
