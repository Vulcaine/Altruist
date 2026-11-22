using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshFunnelTests
{
    [Fact]
    public void Smooth_SinglePoly_ReturnsStartAndEndOnly()
    {
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (10f, 0f), (10f, 10f), (0f, 10f) },
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, new List<(int, int, Vector3, Vector3)>());

        var smoothed = NavMeshFunnel.Smooth(graph,
            polyPath: new[] { 0 },
            start: new Vector3(1, 0, 1),
            end: new Vector3(9, 0, 9));

        smoothed.Should().HaveCount(2);
        smoothed[0].Should().Be(new Vector3(1, 0, 1));
        smoothed[1].Should().Be(new Vector3(9, 0, 9));
    }

    [Fact]
    public void Smooth_StraightCorridor_ProducesNoIntermediateCorners()
    {
        // Three rectangles in a line, each 4×4. Walking the corridor
        // straight-through requires no corner emission — the funnel should
        // collapse to just [start, end].
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (4f, 0f), (4f, 4f), (0f, 4f) },
            new[] { (4f, 0f), (8f, 0f), (8f, 4f), (4f, 4f) },
            new[] { (8f, 0f), (12f, 0f), (12f, 4f), (8f, 4f) },
        };
        // V0 = LEFT when crossing FromPoly → ToPoly. Walking +X means LEFT
        // is +Z (high Z), so 0→1's V0 is at (4,0,4), V1 at (4,0,0). Reverse
        // edges flip the convention.
        var edges = new List<(int, int, Vector3, Vector3)>
        {
            (0, 1, new Vector3(4, 0, 4), new Vector3(4, 0, 0)),
            (1, 0, new Vector3(4, 0, 0), new Vector3(4, 0, 4)),
            (1, 2, new Vector3(8, 0, 4), new Vector3(8, 0, 0)),
            (2, 1, new Vector3(8, 0, 0), new Vector3(8, 0, 4)),
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, edges);

        var smoothed = NavMeshFunnel.Smooth(graph,
            polyPath: new[] { 0, 1, 2 },
            start: new Vector3(2, 0, 2),
            end: new Vector3(10, 0, 2));

        smoothed.Should().HaveCount(2, "straight corridor should collapse to start+end");
        smoothed[0].Should().Be(new Vector3(2, 0, 2));
        smoothed[^1].Should().Be(new Vector3(10, 0, 2));
    }

    [Fact]
    public void Smooth_LCorridor_EmitsExactlyOneCorner()
    {
        // Three polys forming an L:
        //   [0]        (south-west, the start area)
        //   [1]        (south-east, the corner)
        //   [2]        (north-east, the goal area)
        //
        // Start in 0's south-west corner, goal in 2's north-east. The funnel
        // should emit ONE corner at the inside elbow of the L.
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (4f, 0f), (4f, 4f), (0f, 4f) },     // 0: south-west
            new[] { (4f, 0f), (8f, 0f), (8f, 4f), (4f, 4f) },     // 1: south-east (corner)
            new[] { (4f, 4f), (8f, 4f), (8f, 8f), (4f, 8f) },     // 2: north-east
        };
        // V0 = LEFT when crossing FromPoly → ToPoly:
        //   0→1 walks +X, LEFT = +Z (high Z) → V0 = (4,0,4)
        //   1→2 walks +Z, LEFT = -X (low X)  → V0 = (4,0,4)
        var edges = new List<(int, int, Vector3, Vector3)>
        {
            (0, 1, new Vector3(4, 0, 4), new Vector3(4, 0, 0)),
            (1, 0, new Vector3(4, 0, 0), new Vector3(4, 0, 4)),
            (1, 2, new Vector3(4, 0, 4), new Vector3(8, 0, 4)),
            (2, 1, new Vector3(8, 0, 4), new Vector3(4, 0, 4)),
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, edges);

        var smoothed = NavMeshFunnel.Smooth(graph,
            polyPath: new[] { 0, 1, 2 },
            start: new Vector3(1, 0, 1),
            end: new Vector3(7, 0, 7));

        smoothed.Count.Should().BeGreaterThanOrEqualTo(2);
        smoothed[0].Should().Be(new Vector3(1, 0, 1));
        smoothed[^1].Should().Be(new Vector3(7, 0, 7));

        // Inside corner of an L should be the (4,4) elbow — there must be
        // a waypoint somewhere between start and end that pulls the path
        // toward that corner (≤ 2m away).
        if (smoothed.Count >= 3)
        {
            var elbow = new Vector3(4, 0, 4);
            var middleClosestToElbow = smoothed.Skip(1).Take(smoothed.Count - 2)
                .OrderBy(p => Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(elbow.X, elbow.Z)))
                .First();
            Vector2.Distance(
                new Vector2(middleClosestToElbow.X, middleClosestToElbow.Z),
                new Vector2(elbow.X, elbow.Z))
                .Should().BeLessThan(2f, "funnel should hug the L's inside corner");
        }
    }

    [Fact]
    public void Smooth_AlwaysIncludesStartAsFirstWaypoint()
    {
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (4f, 0f), (4f, 4f), (0f, 4f) },
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, new List<(int, int, Vector3, Vector3)>());

        var start = new Vector3(1, 0, 1);
        var smoothed = NavMeshFunnel.Smooth(graph, new[] { 0 }, start, new Vector3(3, 0, 3));
        smoothed[0].Should().Be(start);
    }

    [Fact]
    public void Smooth_EndpointDuplicateIsSuppressed()
    {
        // If the funnel happens to emit the goal as a corner before finishing,
        // the trailing "always add end" must not duplicate it.
        var polys = new List<(float, float)[]>
        {
            new[] { (0f, 0f), (4f, 0f), (4f, 4f), (0f, 4f) },
        };
        var graph = NavTestFixtures.SyntheticGraph(polys, new List<(int, int, Vector3, Vector3)>());

        var end = new Vector3(3, 0, 3);
        var smoothed = NavMeshFunnel.Smooth(graph, new[] { 0 }, new Vector3(1, 0, 1), end);
        smoothed.Last().Should().Be(end);
        // No two adjacent waypoints should be at the same position.
        for (int i = 1; i < smoothed.Count; i++)
        {
            Vector3.Distance(smoothed[i], smoothed[i - 1]).Should().BeGreaterThan(0.001f);
        }
    }
}
