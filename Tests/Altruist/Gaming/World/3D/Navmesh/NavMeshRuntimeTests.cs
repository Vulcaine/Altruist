using System.Numerics;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Gaming.World.Navmesh;

public class NavMeshRuntimeTests
{
    private static (NavMeshService svc, NavMeshRuntime runtime, FakeBody body) NewRuntime(Vector3 startAt)
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
        svc.RegisterMesh("z", mesh);

        var runtime = new NavMeshRuntime(svc, NullLoggerFactory.Instance);
        var body = new FakeBody { Position = startAt };
        return (svc, runtime, body);
    }

    [Fact]
    public void RegisterAgent_ManualAgent_Throws()
    {
        var svc = new NavMeshService(NullLoggerFactory.Instance);
        var runtime = new NavMeshRuntime(svc, NullLoggerFactory.Instance);
        var manualAgent = new NavMeshAgent(svc, "z"); // no body → manual mode

        Assert.Throws<ArgumentException>(() => runtime.RegisterAgent(manualAgent));
    }

    [Fact]
    public void CreateAgent_RegistersImmediately()
    {
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        runtime.RegisteredCount.Should().Be(0);

        var agent = runtime.CreateAgent("z", body, speed: 5f);
        runtime.RegisteredCount.Should().Be(1);
        agent.Body.Should().BeSameAs(body);
        agent.Speed.Should().Be(5f);
    }

    [Fact]
    public void RemoveAgent_ClearsRegistrationAndZeroesVelocity()
    {
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        var agent = runtime.CreateAgent("z", body, speed: 5f);
        agent.SetDestination(body.Position, new Vector3(12, 0, 12));
        runtime.Update(0.1f);
        body.LinearVelocity.Should().NotBe(Vector3.Zero);

        runtime.RemoveAgent(agent);
        runtime.RegisteredCount.Should().Be(0);
        body.LinearVelocity.Should().Be(Vector3.Zero);
    }

    [Fact]
    public void Update_SetsBodyVelocityTowardNextWaypoint()
    {
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        var agent = runtime.CreateAgent("z", body, speed: 4f);
        agent.SetDestination(body.Position, new Vector3(14, 0, 2)).Should().BeTrue();

        runtime.Update(0.1f);

        // Velocity should point roughly +X (toward the goal), magnitude ≈ speed.
        body.LinearVelocity.X.Should().BeGreaterThan(0f);
        var horizSpeed = MathF.Sqrt(body.LinearVelocity.X * body.LinearVelocity.X + body.LinearVelocity.Z * body.LinearVelocity.Z);
        horizSpeed.Should().BeApproximately(4f, 0.01f);
    }

    [Fact]
    public void Update_NoPath_VelocityStaysZero()
    {
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        runtime.CreateAgent("z", body, speed: 5f); // no SetDestination
        runtime.Update(0.1f);
        body.LinearVelocity.Should().Be(Vector3.Zero);
    }

    [Fact]
    public void Update_PreservesBodyYVelocity()
    {
        // Gravity (or kinematic Y motion) is the body owner's job; the
        // runtime must not overwrite the body's Y velocity when steering
        // horizontally.
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        body.LinearVelocity = new Vector3(0, -9.8f, 0);
        var agent = runtime.CreateAgent("z", body, speed: 4f);
        agent.SetDestination(body.Position, new Vector3(14, 0, 14));

        runtime.Update(0.1f);

        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, 0.001f, "Y velocity belongs to gravity/jumps, not navmesh");
    }

    [Fact]
    public void Update_TicksMultipleAgentsIndependently()
    {
        var (svc, runtime, _) = NewRuntime(Vector3.Zero);
        var bodyA = new FakeBody { Position = new Vector3(2, 0, 2) };
        var bodyB = new FakeBody { Position = new Vector3(14, 0, 14) };

        var a = runtime.CreateAgent("z", bodyA, 5f);
        var b = runtime.CreateAgent("z", bodyB, 5f);

        a.SetDestination(bodyA.Position, new Vector3(14, 0, 2));
        b.SetDestination(bodyB.Position, new Vector3(2, 0, 14));

        runtime.Update(0.1f);

        bodyA.LinearVelocity.X.Should().BeGreaterThan(0f, "A heads east");
        bodyB.LinearVelocity.X.Should().BeLessThan(0f, "B heads west");
    }

    [Fact]
    public void Update_FullPathLoop_BodyReachesDestination()
    {
        var (_, runtime, body) = NewRuntime(new Vector3(2, 0, 2));
        var agent = runtime.CreateAgent("z", body, speed: 10f);
        agent.ArrivalRadius = 0.6f;
        var goal = new Vector3(14, 0, 14);
        agent.SetDestination(body.Position, goal);

        const float dt = 0.1f;
        for (int i = 0; i < 200; i++)
        {
            runtime.Update(dt);
            // Manual physics-step replacement: integrate XZ velocity into position.
            var v = body.LinearVelocity;
            body.Position = new Vector3(body.Position.X + v.X * dt, body.Position.Y, body.Position.Z + v.Z * dt);
            if (!agent.HasPath) break;
        }

        agent.HasPath.Should().BeFalse("agent must consume its path");
        Vector2.Distance(new Vector2(body.Position.X, body.Position.Z), new Vector2(goal.X, goal.Z))
            .Should().BeLessThan(2f);
    }

    [Fact]
    public void Update_AgentTickThrows_RemovesItAndDoesNotFaultOthers()
    {
        var (svc, runtime, _) = NewRuntime(Vector3.Zero);
        // A normal agent + a sabotaged agent whose body throws on velocity set.
        var ok = new FakeBody { Position = new Vector3(2, 0, 2) };
        var bad = new ThrowingBody { Position = new Vector3(2, 0, 2) };
        var goodAgent = runtime.CreateAgent("z", ok, 5f);
        var badAgent = runtime.CreateAgent("z", bad, 5f);
        goodAgent.SetDestination(ok.Position, new Vector3(14, 0, 2));
        badAgent.SetDestination(bad.Position, new Vector3(14, 0, 2));

        runtime.Update(0.1f);

        runtime.RegisteredCount.Should().Be(1, "bad agent should have been evicted");
        ok.LinearVelocity.X.Should().BeGreaterThan(0f, "good agent kept moving");
    }

}
