using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshGraphTests
{
    private static NavMeshGraph BuildOpenField()
        => NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] { "....", "....", "....", "...." }),
            NavTestFixtures.FlatTerrain(5f))!;

    [Fact]
    public void TryLocate_PointInsideGrid_FindsPolygon()
    {
        var mesh = BuildOpenField();
        // Center of the grid: (4, _, 4) since cellScale=2 and grid is 4×4 cells = 8m × 8m.
        mesh.TryLocate(new Vector3(4f, 0f, 4f), out int polyIdx).Should().BeTrue();
        polyIdx.Should().Be(0);
    }

    [Fact]
    public void TryLocate_PointOutsideAllPolygons_ReturnsFalse()
    {
        var mesh = BuildOpenField();
        // Grid is (0,0)–(8,8). Point (-5, 0, 4) is outside.
        mesh.TryLocate(new Vector3(-5f, 0f, 4f), out _).Should().BeFalse();
    }

    [Fact]
    public void TrySamplePosition_PointInsidePoly_ReturnsExactPositionWithCorrectY()
    {
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] { "..", ".." }),
            NavTestFixtures.FlatTerrain(7f))!;

        mesh.TrySamplePosition(new Vector3(2f, 99f, 2f), maxDistance: 1f, out int polyIdx, out var on)
            .Should().BeTrue();
        polyIdx.Should().BeGreaterThanOrEqualTo(0);
        on.X.Should().BeApproximately(2f, 0.001f);
        on.Z.Should().BeApproximately(2f, 0.001f);
        // Y comes from the polygon (terrain Y=7), NOT from the input point's Y=99.
        on.Y.Should().BeApproximately(7f, 0.001f);
    }

    [Fact]
    public void TrySamplePosition_PointOffMeshButCloseEnough_SnapsToNearestEdge()
    {
        var mesh = BuildOpenField(); // 8×8m at world origin
        // (10, _, 4) is 2m off the +X edge. With maxDistance=3 should snap.
        mesh.TrySamplePosition(new Vector3(10f, 0f, 4f), maxDistance: 3f, out _, out var on)
            .Should().BeTrue();
        on.X.Should().BeApproximately(8f, 0.001f); // clamped to edge
        on.Z.Should().BeApproximately(4f, 0.001f);
    }

    [Fact]
    public void TrySamplePosition_TooFarFromAnyPoly_ReturnsFalse()
    {
        var mesh = BuildOpenField();
        mesh.TrySamplePosition(new Vector3(1000f, 0f, 1000f), maxDistance: 5f, out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void NavPoly_ContainsXZ_BoundsCheckRejectsObviouslyOutside()
    {
        var poly = new NavPoly(0, new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(2f, 0f, 0f),
            new Vector3(2f, 0f, 2f),
            new Vector3(0f, 0f, 2f),
        });
        poly.ContainsXZ(1f, 1f).Should().BeTrue();
        poly.ContainsXZ(3f, 1f).Should().BeFalse();
        poly.ContainsXZ(-1f, 1f).Should().BeFalse();
    }

    [Fact]
    public void NavPoly_SampleYAt_LerpsAcrossSlope()
    {
        // Polygon with two corners at Y=0 (north edge) and two at Y=10 (south edge).
        // Sampling at the centroid should give Y≈5.
        var poly = new NavPoly(0, new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(2f, 0f, 0f),
            new Vector3(2f, 10f, 2f),
            new Vector3(0f, 10f, 2f),
        });
        poly.SampleYAt(1f, 1f).Should().BeApproximately(5f, 0.5f);
    }

    [Fact]
    public void TryGetPortal_BetweenAdjacentPolys_ReturnsTheirSharedEdge()
    {
        // Build two stacked rects with different terrain Y so they don't merge.
        var grid = NavTestFixtures.GridFromAscii(new[] { "..", ".." });
        var opts = new NavMeshBuilder.Options { MaxIntraRectHeightDelta = 0.01f };
        var mesh = NavMeshBuilder.Build(grid, new ZBumpTerrain(), opts)!;

        mesh.PolyCount.Should().Be(2);
        mesh.TryGetPortal(0, 1, out var portal01).Should().BeTrue();
        portal01.FromPoly.Should().Be(0);
        portal01.ToPoly.Should().Be(1);
        // Reverse direction is a separate edge entry.
        mesh.TryGetPortal(1, 0, out var portal10).Should().BeTrue();
        portal10.FromPoly.Should().Be(1);
        portal10.ToPoly.Should().Be(0);
    }

    [Fact]
    public void TryGetPortal_BetweenNonAdjacentPolys_ReturnsFalse()
    {
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] {
                "...",
                "###",
                "...",
            }),
            NavTestFixtures.FlatTerrain(0))!;

        mesh.PolyCount.Should().Be(2);
        mesh.TryGetPortal(0, 1, out _).Should().BeFalse();
    }

    private sealed class ZBumpTerrain : ITerrainProvider
    {
        public bool IsWalkable(float x, float y, float z) => true;
        public float GetHeight(float x, float z) => z; // monotonic in Z, varies past tolerance
    }
}
