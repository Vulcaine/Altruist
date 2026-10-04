using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Altruist.Physx.Fakes;
using Tests.Gaming.World.Navmesh;

namespace Tests.Altruist.ThreeD.Numerics.Trajectory;

public class TrajectoryRuntime3DTests
{
    private const float Eps = 1e-3f;

    private static (TrajectoryRuntime3D runtime, NavMeshService nav) NewRuntime(float terrainY = 0f)
    {
        var nav = new NavMeshService(NullLoggerFactory.Instance);
        var mesh = NavMeshBuilder.Build(
            NavTestFixtures.GridFromAscii(new[] { "........", "........", "........", "........" }),
            NavTestFixtures.FlatTerrain(terrainY))!;
        nav.RegisterMesh("z", mesh);
        return (new TrajectoryRuntime3D(nav, NullLoggerFactory.Instance), nav);
    }

    [Fact]
    public void LaunchParabolic_ZeroPeakProducesStraightLerp()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody { Position = new Vector3(0, 0, 0) };

        var agent = runtime.LaunchParabolic(body,
            start: new Vector3(0, 0, 0), end: new Vector3(10, 0, 0),
            peakHeight: 0f, durationSeconds: 1f);

        runtime.Update(0.5f);
        body.Position.X.Should().BeApproximately(5f, Eps);
        body.Position.Y.Should().BeApproximately(0f, Eps);
        agent.IsActive.Should().BeTrue();
    }

    [Fact]
    public void LaunchParabolic_PeakHeightProducesArc()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };

        runtime.LaunchParabolic(body,
            start: Vector3.Zero, end: new Vector3(10, 0, 0),
            peakHeight: 5f, durationSeconds: 1f);

        runtime.Update(0.4f); // inside hang phase (0.28 ≤ t ≤ 0.55)
        body.Position.Y.Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void Update_FiresOnArriveExactlyOnceAtT1()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody();
        int arriveCount = 0;
        var agent = runtime.LaunchParabolic(body,
            start: Vector3.Zero, end: new Vector3(10, 0, 0),
            peakHeight: 0f, durationSeconds: 0.5f);
        agent.OnArrive = _ => arriveCount++;

        runtime.Update(0.25f);
        arriveCount.Should().Be(0);
        runtime.Update(0.25f);
        arriveCount.Should().Be(1);
        runtime.Update(0.25f);
        arriveCount.Should().Be(1, "agent already arrived; runtime must not double-fire");
        agent.IsActive.Should().BeFalse();
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Cancel_StopsPositionWritesAndRemovesAgent()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        var agent = runtime.LaunchParabolic(body,
            Vector3.Zero, new Vector3(10, 0, 0), 0f, 1f);

        runtime.Update(0.1f);
        var midPos = body.Position;
        runtime.Cancel(agent);
        runtime.Update(0.5f);
        body.Position.Should().Be(midPos, "cancelled agent must not write position");
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Update_ThrowingOnArriveEvictsAgentAndDoesNotFaultOthers()
    {
        var (runtime, _) = NewRuntime();
        var bodyOk = new FakeBody();
        var bodyBad = new FakeBody();
        runtime.LaunchParabolic(bodyOk, Vector3.Zero, new Vector3(10, 0, 0), 0f, 1f);
        var bad = runtime.LaunchParabolic(bodyBad, Vector3.Zero, new Vector3(10, 0, 0), 0f, 1f);
        bad.OnArrive = _ => throw new InvalidOperationException("boom");

        runtime.Update(0.5f);
        runtime.Update(0.5f);
        // bad agent's OnArrive throws, runtime should evict and continue
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void LaunchParabolic_SnapYToNavMeshOverridesCurveY()
    {
        // Mesh is flat at y=42. Curve has peakHeight=10 (would otherwise rise).
        // With snap, body Y should track mesh (42), not the curve.
        var (runtime, _) = NewRuntime(terrainY: 42f);
        var body = new FakeBody { Position = Vector3.Zero };

        runtime.LaunchParabolic(body,
            start: new Vector3(2, 0, 2), end: new Vector3(6, 0, 6),
            peakHeight: 10f, durationSeconds: 1f,
            snapYToNavMesh: true, zone: "z");

        runtime.Update(0.4f); // hang phase — curve Y would be 10
        body.Position.Y.Should().BeApproximately(42f, 0.5f, "snap mode replaces curve Y with mesh Y");
    }

    [Fact]
    public void Update_ZeroDtIsNoOp()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        runtime.LaunchParabolic(body, Vector3.Zero, new Vector3(10, 0, 0), 0f, 1f);

        runtime.Update(0f);
        body.Position.Should().Be(Vector3.Zero);
        runtime.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void LaunchParabolic_RejectsNonPositiveDuration()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody();
        var act = () => runtime.LaunchParabolic(body, Vector3.Zero, Vector3.One, 0f, 0f);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LaunchParabolic_RejectsSnapWithoutZone()
    {
        var (runtime, _) = NewRuntime();
        var body = new FakeBody();
        var act = () => runtime.LaunchParabolic(body, Vector3.Zero, Vector3.One,
            0f, 1f, snapYToNavMesh: true, zone: null);
        act.Should().Throw<ArgumentException>();
    }
}
