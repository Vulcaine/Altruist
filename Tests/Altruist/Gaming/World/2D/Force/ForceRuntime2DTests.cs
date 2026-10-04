using System.Numerics;
using Altruist.Gaming.TwoD;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Gaming.World.TwoD.Force;

public class ForceRuntime2DTests
{
    private const float Eps = 1e-3f;

    private static ForceRuntime2D NewRuntime() => new(NullLoggerFactory.Instance);

    [Fact]
    public void ApplySustained_AdvancesPositionByVelocityTimesDt()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };

        runtime.ApplySustained(body, new Vector2(5f, 0f), durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(2.5f, Eps);
        body.Position.Y.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void ApplySustained_BothAxesMove_NoPreservedAxisIn2D()
    {
        // 2D mirror: there is no Y-preservation analog of 3D, both axes drive
        // motion equally.
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };

        runtime.ApplySustained(body, new Vector2(3f, 4f), durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(1.5f, Eps);
        body.Position.Y.Should().BeApproximately(2f, Eps);
    }

    [Fact]
    public void ApplySustained_DirectionSpeedSugar_NormalizesDirection()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };

        runtime.ApplySustained(body, direction: new Vector2(0f, 5f), speed: 2f, durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.Y.Should().BeApproximately(1f, Eps);
    }

    [Fact]
    public void ApplySustained_ZeroDirection_NoOp()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };

        var agent = runtime.ApplySustained(body, direction: Vector2.Zero, speed: 5f, durationSeconds: 1f);
        runtime.Update(0.5f);

        body.Position.Should().Be(Vector2.Zero);
        agent.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ConcurrentForces_StackAdditively()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };

        runtime.ApplySustained(body, new Vector2(3f, 0f), durationSeconds: 1f);
        runtime.ApplySustained(body, new Vector2(0f, 4f), durationSeconds: 1f);

        runtime.Update(0.5f);

        body.Position.X.Should().BeApproximately(1.5f, Eps);
        body.Position.Y.Should().BeApproximately(2f, Eps);
    }

    [Fact]
    public void ApplySustained_ExpiresAfterDuration()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };
        var agent = runtime.ApplySustained(body, new Vector2(5f, 0f), durationSeconds: 0.5f);

        runtime.Update(0.5f);
        agent.IsActive.Should().BeFalse();
        runtime.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Cancel_StopsPositionWrites()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };
        var agent = runtime.ApplySustained(body, new Vector2(5f, 0f), durationSeconds: 10f);

        runtime.Update(0.1f);
        var mid = body.Position;
        runtime.Cancel(agent);
        runtime.Update(0.5f);

        body.Position.Should().Be(mid);
    }

    [Fact]
    public void CancelAll_RemovesOnlyForcesOnTargetBody()
    {
        var runtime = NewRuntime();
        var bodyA = new FakeBody2D { Position = Vector2.Zero };
        var bodyB = new FakeBody2D { Position = Vector2.Zero };

        runtime.ApplySustained(bodyA, new Vector2(5f, 0f), durationSeconds: 10f);
        runtime.ApplySustained(bodyA, new Vector2(0f, 3f), durationSeconds: 10f);
        runtime.ApplySustained(bodyB, new Vector2(0f, 7f), durationSeconds: 10f);

        runtime.CancelAll(bodyA);

        runtime.ActiveCount.Should().Be(1);
        runtime.Update(0.5f);
        bodyA.Position.Should().Be(Vector2.Zero);
        bodyB.Position.Y.Should().BeApproximately(3.5f, Eps);
    }

    [Fact]
    public void OnApplied_FiresEveryTickWithDelta()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };
        var deltas = new List<Vector2>();

        var agent = runtime.ApplySustained(body, new Vector2(10f, 0f), durationSeconds: 1f);
        agent.OnApplied = (_, d) => deltas.Add(d);

        runtime.Update(0.25f);
        runtime.Update(0.25f);

        deltas.Should().HaveCount(2);
        deltas[0].X.Should().BeApproximately(2.5f, Eps);
        deltas[1].X.Should().BeApproximately(2.5f, Eps);
    }

    [Fact]
    public void Update_ZeroDtIsNoOp()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D { Position = Vector2.Zero };
        runtime.ApplySustained(body, new Vector2(5f, 0f), durationSeconds: 1f);

        runtime.Update(0f);
        body.Position.Should().Be(Vector2.Zero);
        runtime.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void ApplySustained_RejectsNonPositiveDuration()
    {
        var runtime = NewRuntime();
        var body = new FakeBody2D();
        var act = () => runtime.ApplySustained(body, new Vector2(1f, 0f), 0f);
        act.Should().Throw<ArgumentException>();
    }
}
