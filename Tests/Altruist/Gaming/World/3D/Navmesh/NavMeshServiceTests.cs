using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshServiceTests
{
    private static NavMeshGraph BuildTestMesh(string[] rows)
        => NavMeshBuilder.Build(NavTestFixtures.GridFromAscii(rows), NavTestFixtures.FlatTerrain(0))!;

    [Fact]
    public void RegisterMesh_ExposesMeshByZoneName()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = BuildTestMesh(new[] { "..", ".." });

        svc.RegisterMesh("zone_a", mesh);
        svc.HasMesh("zone_a").Should().BeTrue();
        svc.GetMesh("zone_a").Should().BeSameAs(mesh);
        svc.GetMesh("zone_b").Should().BeNull();
    }

    [Fact]
    public void UnregisterMesh_RemovesIt()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("zone_a", BuildTestMesh(new[] { "..", ".." }));
        svc.UnregisterMesh("zone_a");
        svc.HasMesh("zone_a").Should().BeFalse();
    }

    [Fact]
    public void RegisterMesh_NullThrows()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        Assert.Throws<ArgumentNullException>(() => svc.RegisterMesh("z", null!));
    }

    [Fact]
    public void FindPath_UnknownZone_ReturnsEmpty()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var path = svc.FindPath("missing", new Vector3(0, 0, 0), new Vector3(5, 0, 5));
        path.Waypoints.Should().BeEmpty();
        path.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void FindPath_StartOffMesh_ReturnsEmpty()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", BuildTestMesh(new[] { "..", ".." }));
        // Start point is way outside the (0,0)-(4,4) mesh, with a small snap distance.
        var path = svc.FindPath("z", new Vector3(1000, 0, 1000), new Vector3(2, 0, 2), snapDistance: 1f);
        path.Waypoints.Should().BeEmpty();
    }

    [Fact]
    public void FindPath_ValidStartAndEnd_ReturnsCompletePath()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = BuildTestMesh(new[] { "....", "....", "....", "...." });
        svc.RegisterMesh("z", mesh);

        var path = svc.FindPath("z", new Vector3(1, 0, 1), new Vector3(7, 0, 7));
        path.Waypoints.Should().NotBeEmpty();
        path.IsComplete.Should().BeTrue();
        path.Waypoints[0].X.Should().BeApproximately(1f, 0.5f);
        path.Waypoints[0].Z.Should().BeApproximately(1f, 0.5f);
        path.Waypoints[^1].X.Should().BeApproximately(7f, 0.5f);
        path.Waypoints[^1].Z.Should().BeApproximately(7f, 0.5f);
    }

    [Fact]
    public void FindPath_AcrossObstacle_LengthExceedsStraightLine()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] {
                ".....",
                "..#..",
                "..#..",
                "..#..",
                ".....",
            }),
            NavTestFixtures.FlatTerrain(0))!;
        svc.RegisterMesh("z", mesh);

        var start = new Vector3(1, 0, 1);
        var goal = new Vector3(9, 0, 9);
        var straight = Vector3.Distance(start, goal);

        var path = svc.FindPath("z", start, goal);
        path.IsComplete.Should().BeTrue();
        path.Length.Should().BeGreaterThan(straight, "must detour around the wall");
    }

    [Fact]
    public void TrySamplePosition_OffMesh_FallsBackToInputAndFalse()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", BuildTestMesh(new[] { "..", ".." }));

        var ok = svc.TrySamplePosition("z", new Vector3(500, 0, 500), maxDistance: 1f, out var on);
        ok.Should().BeFalse();
        on.Should().Be(new Vector3(500, 0, 500));
    }

    [Fact]
    public void IsLineWalkable_StraightLineThroughOpenSpace_True()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", BuildTestMesh(new[] { "....", "....", "....", "...." }));

        svc.IsLineWalkable("z", new Vector3(1, 0, 1), new Vector3(6, 0, 6)).Should().BeTrue();
    }

    [Fact]
    public void IsLineWalkable_LineCrossesObstacle_False()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] {
                ".....",
                "..#..",
                "..#..",
                ".....",
            }),
            NavTestFixtures.FlatTerrain(0))!;
        svc.RegisterMesh("z", mesh);

        // Straight line from west side to east side punches through the
        // wall at column 2 — no walkable poly there.
        svc.IsLineWalkable("z", new Vector3(1, 0, 3), new Vector3(9, 0, 3)).Should().BeFalse();
    }

    [Fact]
    public void Concurrent_FindPath_NoCrashWithThreadLocalPathfinder()
    {
        // Pathfinder is per-thread. Hammer it from many tasks; if the pool
        // is wired correctly nothing throws and every result completes.
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", BuildTestMesh(new[] {
            "....",
            "....",
            "....",
            "....",
        }));

        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            var p = svc.FindPath("z", new Vector3(1, 0, 1), new Vector3(6, 0, 6));
            return p.IsComplete;
        })).ToArray();

        Task.WaitAll(tasks);
        tasks.All(t => t.Result).Should().BeTrue();
    }
}
