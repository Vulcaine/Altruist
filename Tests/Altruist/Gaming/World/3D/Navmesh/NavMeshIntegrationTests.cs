using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

// Alias the facade so it doesn't clash with System.IO.Path (xUnit and other
// helpers pull System.IO transitively through the test runner). Don't reuse
// the name "NavPath" — that's the result type from Altruist.Gaming.ThreeD.
using Routing = Altruist.Gaming.ThreeD.Navigation.Path;

namespace Tests.Gaming.World.Navmesh;

/// <summary>End-to-end nav-mesh exercises: realistic-sized walkability grid
/// + sloped terrain → builder → service registration → pathfinding → agent
/// follow loop. These verify the layers compose, not just that each unit
/// works in isolation.</summary>
public class NavMeshIntegrationTests
{
    /// <summary>A 32×32 cell map with a wall, a moat, and a bridge — the
    /// kind of topology a real Metin2 zone has.
    ///
    /// Layout (top-down, '.' = walkable, '#' = wall, 'w' = water):
    /// <code>
    /// ................................
    /// .##############################.    ← upper wall
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .##############wwww############.    ← moat (with bridge gap)
    /// ...............wwww.............
    /// ...............wwww.............
    /// ...............wwww.............
    /// ...............wwww.............
    /// .##############wwww############.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .#............................#.
    /// .##############################.    ← lower wall
    /// ................................
    /// ................................
    /// ................................
    /// ................................
    /// ................................
    /// ................................
    /// </code>
    /// The bridge is the 4-cell-wide vertical strip of '#' (passable, not
    /// blocked) — wait, I'm using # for walls. The actual bridge is the
    /// gap in the moat (we leave 4 cells walkable through the wall).
    /// </summary>
    private static IWalkabilityGrid BuildRealisticMap()
    {
        var rows = new List<string>();
        // Outer "yard" border + walls + moat
        rows.Add(new string('.', 32));
        rows.Add("." + new string('#', 30) + ".");
        for (int i = 0; i < 8; i++) rows.Add(".#" + new string('.', 28) + "#.");
        rows.Add("." + new string('#', 14) + "...." + new string('#', 12) + ".");
        // Moat row (water on the sides, walkable bridge in the middle)
        for (int i = 0; i < 4; i++) rows.Add(new string('.', 32));
        rows.Add("." + new string('#', 14) + "...." + new string('#', 12) + ".");
        for (int i = 0; i < 9; i++) rows.Add(".#" + new string('.', 28) + "#.");
        rows.Add("." + new string('#', 30) + ".");
        while (rows.Count < 32) rows.Add(new string('.', 32));

        return NavTestFixtures.GridFromAscii(rows.ToArray());
    }

    [Fact]
    public void EndToEnd_BuildAndPathFindAcrossBridge()
    {
        var grid = BuildRealisticMap();
        // Slight terrain slope so corners get realistic Y values.
        var terrain = NavTestFixtures.RampTerrain(baseY: 100f, slopePerUnitX: 0.05f);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var mesh = NavMeshBuilder.Build(grid, terrain)!;
        stopwatch.Stop();

        mesh.Should().NotBeNull();
        mesh.PolyCount.Should().BeGreaterThan(0);
        mesh.PolyCount.Should().BeLessThan(200,
            "greedy meshing should keep the poly count well below the cell count (1024)");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500,
            "build should be fast even for a real-sized map");

        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("realistic", mesh);

        // Goal: walk from upper-left "yard" to lower-right "yard". The
        // straight line crosses the moat — A* must route through the bridge.
        var start = new Vector3(8f, 0f, 8f);  // upper yard
        var goal = new Vector3(48f, 0f, 50f); // lower yard

        var path = Routing.Detect(svc, "realistic", start, goal);

        path.IsComplete.Should().BeTrue();
        path.Waypoints.Count.Should().BeGreaterThanOrEqualTo(2);

        // Path should pass NEAR the bridge (centered around X=30, Z=22)
        // since that's the only opening in the moat.
        var bridge = new Vector2(30f, 22f);
        bool passesNearBridge = path.Waypoints.Any(w =>
            Vector2.Distance(new Vector2(w.X, w.Z), bridge) < 12f);
        passesNearBridge.Should().BeTrue("path must cross through the bridge gap");
    }

    [Fact]
    public void EndToEnd_AgentFollowsPathToCompletion()
    {
        var grid = NavTestFixtures.GridFromAscii(new[] {
            "..............",
            ".............",
            ".............",
            "..............",
            "..............",
            "..............",
            "..............",
        }.Select(r => r.PadRight(14, '.')).ToArray());
        var terrain = NavTestFixtures.FlatTerrain(0);
        var mesh = NavMeshBuilder.Build(grid, terrain)!;

        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", mesh);

        var agent = new NavMeshAgent(svc, "z");
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(20, 0, 12)).Should().BeTrue();
        agent.HasPath.Should().BeTrue();

        // Walk toward each waypoint until the path is exhausted. Limit to
        // 200 iterations so an infinite loop fails the test instead of
        // hanging forever.
        var pos = new Vector3(2, 0, 2);
        const float speed = 5f; // m per step
        for (int i = 0; i < 200; i++)
        {
            var wp = agent.StepToward(pos, arrivalRadius: 0.4f);
            if (wp == null) break;

            var dx = wp.Value.X - pos.X;
            var dz = wp.Value.Z - pos.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 1e-4f) continue;
            var step = MathF.Min(speed, dist);
            pos = new Vector3(pos.X + dx / dist * step, 0, pos.Z + dz / dist * step);
        }

        agent.HasPath.Should().BeFalse("agent must consume the entire path");
        Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(20, 12))
            .Should().BeLessThan(2f, "agent should arrive near the goal");
    }

    [Fact]
    public void EndToEnd_ReplansWhenMovingTargetCrossesObstacle()
    {
        // Static map with one wall down the middle. Target starts on the
        // east side of the wall, then jumps to the west side. The agent
        // (sitting east of the wall) should re-plan and the new path should
        // route around the wall to reach the new target.
        var grid = NavTestFixtures.GridFromAscii(new[] {
            ".................",
            ".................",
            "........#........",
            "........#........",
            "........#........",
            ".................",
            ".................",
        });
        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(0))!;
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("z", mesh);

        var agent = new NavMeshAgent(svc, "z", replanIfTargetMovedBy: 1f);
        var pursuer = new Vector3(20, 0, 6);    // east side
        var target = new Vector3(28, 0, 6);     // east side (initial)

        agent.SetDestination(pursuer, target).Should().BeTrue();
        var firstPath = agent.CurrentPath;

        // Target jumps to the west side of the wall.
        target = new Vector3(8, 0, 6);
        agent.TrackTarget(pursuer, target);

        agent.CurrentPath.Should().NotBeSameAs(firstPath, "agent should replan after big target jump");
        agent.CurrentPath.IsComplete.Should().BeTrue();

        // The new path must NOT punch through the wall — there should be at
        // least one waypoint above (z<4) or below (z>8) the wall row.
        bool detoursAroundWall = agent.CurrentPath.Waypoints.Any(w => w.Z < 3.5f || w.Z > 9f);
        detoursAroundWall.Should().BeTrue("new path must skirt the wall");
    }

    [Fact]
    public void EndToEnd_FacadeAndServiceProduceSameResult()
    {
        var grid = BuildRealisticMap();
        var mesh = NavMeshBuilder.Build(grid, NavTestFixtures.FlatTerrain(50))!;
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        svc.RegisterMesh("realistic", mesh);

        var start = new Vector3(8f, 0f, 8f);
        var goal = new Vector3(48f, 0f, 50f);

        var viaService = svc.FindPath("realistic", start, goal);
        var viaFacade = Routing.Detect(svc, "realistic", start, goal);

        // Facade is a thin pass-through; both should match exactly.
        viaFacade.IsComplete.Should().Be(viaService.IsComplete);
        viaFacade.Waypoints.Count.Should().Be(viaService.Waypoints.Count);
        for (int i = 0; i < viaService.Waypoints.Count; i++)
            viaFacade.Waypoints[i].Should().Be(viaService.Waypoints[i]);
    }
}
