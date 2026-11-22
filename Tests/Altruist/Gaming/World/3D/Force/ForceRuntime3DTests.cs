using System.Numerics;
using Altruist.Gaming.ThreeD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Gaming.World.ThreeD.Force;

public class ForceRuntime3DTests
{
    private const float Eps = 1e-3f;

    private static ForceRuntime3D NewRuntime() => new(NullLoggerFactory.Instance);

    [Fact]
    public void ApplySustained_AdvancesPositionByVelocityTimesDt()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };

        runtime.ApplySustained(body, new Vector3(5f, 0f, 0f), durationSeconds: 1f);

        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(2.5f, Eps);
        body.Position.Y.Should().BeApproximately(0f, Eps);
        body.Position.Z.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void ApplySustained_VelocityYIsIgnored_BodyYUnchanged()
    {
        // 3D forces are XZ-planar; Y is preserved so non-KCC bodies keep
        // gravity and KCC bodies keep their vertical state authoritative.
        var runtime = NewRuntime();
        var body = new FakeBody { Position = new Vector3(0f, 100f, 0f) };

        runtime.ApplySustained(body, new Vector3(0f, 999f, 5f), durationSeconds: 1f);
        runtime.Update(0.4f);

        body.Position.Y.Should().BeApproximately(100f, Eps,
            "Y component of force velocity must be ignored");
        body.Position.Z.Should().BeApproximately(2f, Eps);
    }

    [Fact]
    public void ApplySustained_DirectionSpeedSugar_NormalizesDirection()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };

        // direction has magnitude 5 — should be normalized to unit before
        // multiplying by speed=2 → effective velocity 2 m/s on X.
        runtime.ApplySustained(body, direction: new Vector3(5f, 0f, 0f), speed: 2f, durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(1f, Eps);
    }

    [Fact]
    public void ApplySustained_ZeroDirection_NoOp()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };

        var agent = runtime.ApplySustained(body, direction: Vector3.Zero, speed: 5f, durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.Should().Be(Vector3.Zero);
        agent.IsActive.Should().BeFalse();
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void ApplySustained_ExpiresAfterDuration()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        var agent = runtime.ApplySustained(body, new Vector3(5f, 0f, 0f), durationSeconds: 1f);

        runtime.Update(0.5f);
        agent.IsActive.Should().BeTrue();
        runtime.ActiveCount.Should().Be(1);

        runtime.Update(0.5f);
        agent.IsActive.Should().BeFalse();
        runtime.ActiveCount.Should().Be(0);

        // Further updates must not move the body any more.
        runtime.Update(1.0f);
        body.Position.X.Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void ConcurrentForces_StackAdditively()
    {
        // Two forces on the same body should sum velocities each tick.
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };

        runtime.ApplySustained(body, new Vector3(3f, 0f, 0f), durationSeconds: 1f);
        runtime.ApplySustained(body, new Vector3(0f, 0f, 4f), durationSeconds: 1f);

        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(1.5f, Eps);
        body.Position.Z.Should().BeApproximately(2f, Eps);
    }

    [Fact]
    public void OnApplied_FiresEveryTickWithDelta()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        var deltas = new List<Vector3>();

        var agent = runtime.ApplySustained(body, new Vector3(10f, 0f, 0f), durationSeconds: 1f);
        agent.OnApplied = (_, d) => deltas.Add(d);

        runtime.Update(0.25f);
        runtime.Update(0.25f);

        deltas.Should().HaveCount(2);
        deltas[0].X.Should().BeApproximately(2.5f, Eps);
        deltas[0].Y.Should().Be(0f);
        deltas[1].X.Should().BeApproximately(2.5f, Eps);
    }

    [Fact]
    public void OnExpired_FiresExactlyOnceWhenAgentEnds()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        int expiredCount = 0;

        var agent = runtime.ApplySustained(body, new Vector3(5f, 0f, 0f), durationSeconds: 0.5f);
        agent.OnExpired = _ => expiredCount++;

        runtime.Update(0.25f);
        expiredCount.Should().Be(0);

        runtime.Update(0.25f);
        expiredCount.Should().Be(1);

        runtime.Update(0.25f);
        expiredCount.Should().Be(1, "expired must not double-fire");
    }

    [Fact]
    public void Cancel_StopsPositionWritesAndFiresOnExpired()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        int expiredCount = 0;

        var agent = runtime.ApplySustained(body, new Vector3(5f, 0f, 0f), durationSeconds: 10f);
        agent.OnExpired = _ => expiredCount++;

        runtime.Update(0.1f);
        var midPos = body.Position;

        runtime.Cancel(agent);
        expiredCount.Should().Be(1);
        runtime.Update(0.5f);

        body.Position.Should().Be(midPos, "cancelled agent must not advance position");
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void CancelAll_RemovesOnlyForcesOnTheTargetBody()
    {
        var runtime = NewRuntime();
        var bodyA = new FakeBody { Position = Vector3.Zero };
        var bodyB = new FakeBody { Position = Vector3.Zero };

        runtime.ApplySustained(bodyA, new Vector3(5f, 0f, 0f), durationSeconds: 10f);
        runtime.ApplySustained(bodyA, new Vector3(0f, 0f, 3f), durationSeconds: 10f);
        runtime.ApplySustained(bodyB, new Vector3(0f, 0f, 7f), durationSeconds: 10f);

        runtime.CancelAll(bodyA);

        runtime.ActiveCount.Should().Be(1);
        runtime.Update(0.5f);
        bodyA.Position.Should().Be(Vector3.Zero);
        bodyB.Position.Z.Should().BeApproximately(3.5f, Eps);
    }

    [Fact]
    public void Update_ZeroDtIsNoOp()
    {
        var runtime = NewRuntime();
        var body = new FakeBody { Position = Vector3.Zero };
        runtime.ApplySustained(body, new Vector3(5f, 0f, 0f), durationSeconds: 1f);

        runtime.Update(0f);

        body.Position.Should().Be(Vector3.Zero);
        runtime.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Update_ThrowingOnAppliedEvictsAgentDoesNotFaultOthers()
    {
        var runtime = NewRuntime();
        var bodyOk = new FakeBody { Position = Vector3.Zero };
        var bodyBad = new FakeBody { Position = Vector3.Zero };

        runtime.ApplySustained(bodyOk, new Vector3(5f, 0f, 0f), durationSeconds: 1f);
        var bad = runtime.ApplySustained(bodyBad, new Vector3(5f, 0f, 0f), durationSeconds: 1f);
        bad.OnApplied = (_, _) => throw new InvalidOperationException("boom");

        runtime.Update(0.5f);

        bodyOk.Position.X.Should().BeApproximately(2.5f, Eps,
            "well-behaved agent must advance even when sibling's callback throws");
        runtime.ActiveCount.Should().Be(1, "faulty agent must be evicted");
    }

    [Fact]
    public void ApplySustained_RejectsNonPositiveDuration()
    {
        var runtime = NewRuntime();
        var body = new FakeBody();
        var act = () => runtime.ApplySustained(body, new Vector3(1f, 0f, 0f), 0f);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplySustained_RejectsNullBody()
    {
        var runtime = NewRuntime();
        var act = () => runtime.ApplySustained(null!, new Vector3(1f, 0f, 0f), 1f);
        act.Should().Throw<ArgumentNullException>();
    }
}
