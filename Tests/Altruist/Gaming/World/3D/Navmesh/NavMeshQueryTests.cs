using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.World.Navmesh;

/// <summary>Tests for the extended INavMeshService query surface — raycast
/// with hit position, area-weighted random points, near-circle random
/// points, and cheap reachability.</summary>
public class NavMeshQueryTests
{
    private static NavMeshService NewService(string[] rows)
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = NavMeshBuilder.Build(NavTestFixtures.GridFromAscii(rows), NavTestFixtures.FlatTerrain(0))!;
        svc.RegisterMesh("z", mesh);
        return svc;
    }

    // ── TryRaycast ───────────────────────────────────────────────────────

    [Fact]
    public void TryRaycast_ClearLineThroughOpenSpace_ReturnsTrueAtEnd()
    {
        var svc = NewService(new[] { "....", "....", "....", "...." });
        svc.TryRaycast("z", new Vector3(1, 0, 1), new Vector3(6, 0, 6), out var hit).Should().BeTrue();
        hit.X.Should().BeApproximately(6f, 0.001f);
        hit.Z.Should().BeApproximately(6f, 0.001f);
    }

    [Fact]
    public void TryRaycast_BlockedByObstacle_ReturnsFalseWithLastOnMeshHit()
    {
        // Wall at column 2 from row 1 to row 2.
        var svc = NewService(new[] {
            ".....",
            "..#..",
            "..#..",
            ".....",
        });
        // Cast straight east through the wall row.
        svc.TryRaycast("z", new Vector3(1, 0, 3), new Vector3(9, 0, 3), out var hit).Should().BeFalse();
        // Hit should be ON the mesh (i.e. before the wall starts), not past it.
        hit.X.Should().BeLessThan(5f);
    }

    [Fact]
    public void TryRaycast_StartOffMesh_ReturnsFalse()
    {
        var svc = NewService(new[] { "..", ".." });
        svc.TryRaycast("z", new Vector3(100, 0, 100), new Vector3(2, 0, 2), out _).Should().BeFalse();
    }

    [Fact]
    public void TryRaycast_UnknownZone_ReturnsFalse()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.TryRaycast("missing", new Vector3(0, 0, 0), new Vector3(1, 0, 1), out _).Should().BeFalse();
    }

    [Fact]
    public void IsLineWalkable_AndTryRaycast_AgreeOnTrueFalse()
    {
        var svc = NewService(new[] {
            ".....",
            "..#..",
            "..#..",
            ".....",
        });
        var open = (new Vector3(1, 0, 0), new Vector3(8, 0, 0));
        var blocked = (new Vector3(1, 0, 3), new Vector3(9, 0, 3));

        svc.IsLineWalkable("z", open.Item1, open.Item2)
            .Should().Be(svc.TryRaycast("z", open.Item1, open.Item2, out _));
        svc.IsLineWalkable("z", blocked.Item1, blocked.Item2)
            .Should().Be(svc.TryRaycast("z", blocked.Item1, blocked.Item2, out _));
    }

    // ── TryRandomPoint ────────────────────────────────────────────────────

    [Fact]
    public void TryRandomPoint_ReturnsPointInsideMeshBounds()
    {
        var svc = NewService(new[] { "....", "....", "....", "...." });
        var rng = new Random(42);

        for (int i = 0; i < 50; i++)
        {
            svc.TryRandomPoint("z", rng, out var p).Should().BeTrue();
            // Mesh covers (0,0)-(8,8) since cellScale=2.
            p.X.Should().BeInRange(0f, 8f);
            p.Z.Should().BeInRange(0f, 8f);
        }
    }

    [Fact]
    public void TryRandomPoint_AlwaysLandsOnMesh()
    {
        // Map with a dead zone (off-mesh column). Random points must NEVER
        // land in the dead zone.
        var svc = NewService(new[] {
            "....##....",
            "....##....",
            "....##....",
            "....##....",
        });
        // Snap test: random points should always be locatable on the mesh.
        var rng = new Random(1);
        for (int i = 0; i < 100; i++)
        {
            svc.TryRandomPoint("z", rng, out var p).Should().BeTrue();
            // Use the service's sample to verify it's on a poly.
            svc.TrySamplePosition("z", p, maxDistance: 0.01f, out _).Should().BeTrue(
                $"random point ({p.X:F2},{p.Z:F2}) must land on a polygon");
        }
    }

    [Fact]
    public void TryRandomPoint_UnknownZone_ReturnsFalse()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.TryRandomPoint("missing", new Random(), out _).Should().BeFalse();
    }

    // ── TryRandomPointNear ────────────────────────────────────────────────

    [Fact]
    public void TryRandomPointNear_PointWithinRadiusAndOnMesh()
    {
        var svc = NewService(new[] { "........", "........", "........", "........" });
        var center = new Vector3(8, 0, 8);
        const float radius = 3f;

        var rng = new Random(5);
        for (int i = 0; i < 25; i++)
        {
            svc.TryRandomPointNear("z", center, radius, rng, out var p).Should().BeTrue();
            Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(center.X, center.Z))
                .Should().BeLessThanOrEqualTo(radius + 0.5f, "point must be within radius (small slack for snap)");
        }
    }

    [Fact]
    public void TryRandomPointNear_CenterOffMeshCircleClipped_StillSnapsSomething()
    {
        // Center is outside the mesh but the search circle overlaps.
        var svc = NewService(new[] { "....", "....", "....", "...." }); // (0,0)-(8,8)
        var center = new Vector3(-2, 0, 4); // 2m off the west edge
        // Radius 5 reaches into the mesh.
        var rng = new Random(3);
        bool gotOne = false;
        for (int i = 0; i < 50; i++)
        {
            if (svc.TryRandomPointNear("z", center, 5f, rng, out _))
            {
                gotOne = true;
                break;
            }
        }
        gotOne.Should().BeTrue("at least some samples should land in the mesh-overlapping arc");
    }

    [Fact]
    public void TryRandomPointNear_RadiusTooSmallEntirelyOffMesh_ReturnsFalse()
    {
        var svc = NewService(new[] { "..", ".." });
        var center = new Vector3(100, 0, 100);
        svc.TryRandomPointNear("z", center, 1f, new Random(), out _).Should().BeFalse();
    }

    // ── IsReachable ───────────────────────────────────────────────────────

    [Fact]
    public void IsReachable_SameComponent_ReturnsTrue()
    {
        var svc = NewService(new[] { "....", "....", "....", "...." });
        svc.IsReachable("z", new Vector3(1, 0, 1), new Vector3(6, 0, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsReachable_DisconnectedComponents_ReturnsFalse()
    {
        // ###  separator means top and bottom rows are disconnected.
        var svc = NewService(new[] {
            "...",
            "###",
            "...",
        });
        svc.IsReachable("z", new Vector3(1, 0, 0.5f), new Vector3(1, 0, 4.5f)).Should().BeFalse();
    }

    [Fact]
    public void IsReachable_AcrossObstacle_StillReachable()
    {
        var svc = NewService(new[] {
            ".....",
            "..#..",
            "..#..",
            ".....",
        });
        // The wall is finite — A* could route around it.
        svc.IsReachable("z", new Vector3(1, 0, 1), new Vector3(8, 0, 7)).Should().BeTrue();
    }

    [Fact]
    public void IsReachable_MuchCheaperThanFindPath()
    {
        // Smoke test: IsReachable on a big map shouldn't take long.
        var rows = new string[64];
        for (int i = 0; i < 64; i++) rows[i] = new string('.', 64);
        var svc = NewService(rows);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.IsReachable("z", new Vector3(2, 0, 2), new Vector3(120, 0, 120)).Should().BeTrue();
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }
}
