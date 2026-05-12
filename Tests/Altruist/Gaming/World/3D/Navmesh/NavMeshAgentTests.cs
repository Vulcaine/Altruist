using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshAgentTests
{
    private static (NavMeshService svc, NavMeshGraph mesh, string zone) NewService()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] {
                "........",
                "........",
                "........",
                "........",
                "........",
                "........",
                "........",
                "........",
            }),
            NavTestFixtures.FlatTerrain(0))!;
        const string zone = "test_zone";
        svc.RegisterMesh(zone, mesh);
        return (svc, mesh, zone);
    }

    [Fact]
    public void NewAgent_HasNoPath()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);
        agent.HasPath.Should().BeFalse();
        agent.CurrentWaypoint.Should().BeNull();
    }

    [Fact]
    public void SetDestination_ValidPoints_PopulatesWaypoints()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);

        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14)).Should().BeTrue();
        agent.HasPath.Should().BeTrue();
        agent.CurrentWaypoint.Should().NotBeNull();
        agent.CurrentPath.Waypoints.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void SetDestination_OnEmptyZone_ReturnsFalseAndClearsPath()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var agent = new NavMeshAgent(svc, "missing_zone");
        agent.SetDestination(new Vector3(0, 0, 0), new Vector3(10, 0, 10)).Should().BeFalse();
        agent.HasPath.Should().BeFalse();
    }

    [Fact]
    public void StepToward_UntilArrival_AdvancesWaypointCursor()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14));

        // The first waypoint is the next non-start position. Standing on it
        // should advance the cursor.
        var wp = agent.CurrentWaypoint!.Value;
        var arrived = agent.StepToward(wp, arrivalRadius: 0.5f);

        // Either the cursor advanced (next waypoint exposed) or path was the
        // last one and HasPath is now false. Both are valid arrival outcomes.
        if (arrived == null)
        {
            agent.HasPath.Should().BeFalse();
        }
        else
        {
            arrived.Value.Should().NotBe(wp, "cursor must advance after arrival");
        }
    }

    [Fact]
    public void StepToward_FarFromWaypoint_DoesNotAdvance()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14));

        var wpBefore = agent.CurrentWaypoint;
        // Standing far from the waypoint with a tiny arrival radius — agent
        // should still be heading toward the same one.
        var stillTarget = agent.StepToward(new Vector3(0, 0, 0), arrivalRadius: 0.1f);
        stillTarget.Should().Be(wpBefore);
    }

    [Fact]
    public void TrackTarget_TargetMovesPastTolerance_RePlans()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone, replanIfTargetMovedBy: 1f);
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14));

        var firstPath = agent.CurrentPath;

        // Target moved 5m → past the 1m tolerance → must replan.
        agent.TrackTarget(new Vector3(2, 0, 2), new Vector3(14, 0, 4));
        agent.CurrentPath.Should().NotBeSameAs(firstPath, "agent should have replanned");
    }

    [Fact]
    public void TrackTarget_TargetWithinTolerance_DoesNotRePlan()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone, replanIfTargetMovedBy: 2f);
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14));

        var firstPath = agent.CurrentPath;

        // Target moved only 0.5m → under the 2m tolerance → keep current path.
        agent.TrackTarget(new Vector3(2, 0, 2), new Vector3(14.3f, 0, 14.4f));
        agent.CurrentPath.Should().BeSameAs(firstPath);
    }

    [Fact]
    public void TrackTarget_NoExistingPath_PlansFreshly()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);
        agent.HasPath.Should().BeFalse();

        agent.TrackTarget(new Vector3(2, 0, 2), new Vector3(14, 0, 14)).Should().BeTrue();
        agent.HasPath.Should().BeTrue();
    }

    [Fact]
    public void Stop_ClearsPath()
    {
        var (svc, _, zone) = NewService();
        var agent = new NavMeshAgent(svc, zone);
        agent.SetDestination(new Vector3(2, 0, 2), new Vector3(14, 0, 14));
        agent.HasPath.Should().BeTrue();

        agent.Stop();
        agent.HasPath.Should().BeFalse();
        agent.CurrentWaypoint.Should().BeNull();
    }
}
