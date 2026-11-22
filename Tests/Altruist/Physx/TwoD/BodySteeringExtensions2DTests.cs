using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Physx.TwoD;

public class BodySteeringExtensions2DTests
{
    private const float Eps = 1e-3f;

    [Fact]
    public void FaceToward_SnapsRotationTowardTarget()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        body.FaceToward(new Vector2(5f, 0f)); // +X: local +Y turned clockwise a quarter turn
        body.GetRotation().Should().BeApproximately(-MathF.PI / 2f, Eps);
    }

    [Theory]
    [InlineData(5f, 0f)]
    [InlineData(3f, 4f)]
    [InlineData(-2f, 1f)]
    [InlineData(-1f, -3f)]
    public void FaceToward_points_the_counter_clockwise_body_up_axis_at_the_target(float x, float y)
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        body.FaceToward(new Vector2(x, y));

        var up = Rotation2D.FromRadians(body.RotationZ).Rotate(Vector2.UnitY);
        var expected = Vector2.Normalize(new Vector2(x, y));
        up.X.Should().BeApproximately(expected.X, Eps);
        up.Y.Should().BeApproximately(expected.Y, Eps);
        body.IsFacing(new Vector2(x, y), halfAngleDegrees: 1f).Should().BeTrue();
    }

    [Fact]
    public void IsFacing_reads_the_counter_clockwise_body_rotation()
    {
        var body = new FakeBody2D { RotationZ = MathF.PI / 2f }; // local +Y now points at -X
        body.IsFacing(new Vector2(-5f, 0f), halfAngleDegrees: 10f).Should().BeTrue();
        body.IsFacing(new Vector2(5f, 0f), halfAngleDegrees: 10f).Should().BeFalse();
    }

    [Fact]
    public void MoveTowardAngle_with_the_body_rotation_moves_the_body_forward()
    {
        var body = new FakeBody2D { RotationZ = MathF.PI / 2f };
        body.MoveTowardAngle(body.GetRotation(), speed: 2f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(-2f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void FaceToward_NoOpOnCoincidentTarget()
    {
        var body = new FakeBody2D { Position = new Vector2(1, 2), RotationZ = 0f };
        body.FaceToward(new Vector2(1, 2));
        body.RotationZ.Should().Be(0f);
    }

    [Fact]
    public void FaceToward_Position2DOverloadMatchesVector2()
    {
        var bodyV = new FakeBody2D { Position = Vector2.Zero };
        var bodyP = new FakeBody2D { Position = Vector2.Zero };
        bodyV.FaceToward(new Vector2(5f, 0f));
        bodyP.FaceToward(Position2D.Of(5, 0));
        bodyP.RotationZ.Should().BeApproximately(bodyV.RotationZ, Eps);
    }

    [Fact]
    public void TurnToward_ClampsAtMaxAngularSpeed()
    {
        var body = new FakeBody2D { Position = Vector2.Zero, RotationZ = 0f };
        body.TurnToward(new Vector2(1, 0), maxAngularSpeedRadPerSec: 1f, dt: 0.1f);
        body.RotationZ.Should().BeApproximately(-0.1f, Eps);
    }

    [Fact]
    public void TurnToward_NeverOvershoots()
    {
        var body = new FakeBody2D { Position = Vector2.Zero, RotationZ = 0f };
        body.TurnToward(new Vector2(1, 0), maxAngularSpeedRadPerSec: 100f, dt: 1f);
        body.RotationZ.Should().BeApproximately(-MathF.PI / 2f, Eps);
    }

    [Fact]
    public void MoveToward_WritesFullVelocity()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        body.MoveToward(new Vector2(10, 0), speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(5f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void MoveToward_ZeroSpeedStops()
    {
        var body = new FakeBody2D { LinearVelocity = new Vector2(3, 4) };
        body.MoveToward(new Vector2(10, 10), speed: 0f, dt: 0.1f);
        body.LinearVelocity.Should().Be(Vector2.Zero);
    }

    [Fact]
    public void MoveTowardAngle_StepsInGivenRotation()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        // rotation=0 → forward is +Y
        body.MoveTowardAngle(rotationRadians: 0f, speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(0f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void Stop_ZeroesVelocity()
    {
        var body = new FakeBody2D { LinearVelocity = new Vector2(3, 4) };
        body.Stop();
        body.LinearVelocity.Should().Be(Vector2.Zero);
    }

    [Fact]
    public void IsFacing_TargetInFront()
    {
        var body = new FakeBody2D { RotationZ = 0f }; // facing +Y
        body.IsFacing(new Vector2(0.1f, 5f), halfAngleDegrees: 30f).Should().BeTrue();
    }

    [Fact]
    public void IsFacing_TargetBehind()
    {
        var body = new FakeBody2D { RotationZ = 0f };
        body.IsFacing(new Vector2(0, -5f), halfAngleDegrees: 30f).Should().BeFalse();
    }

    [Fact]
    public void DistanceTo_FullDistance()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        body.DistanceTo(new Vector2(3, 4)).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void DistanceTo_Position2DOverloadMatchesVector2()
    {
        var body = new FakeBody2D { Position = new Vector2(1, 2) };
        body.DistanceTo(Position2D.Of(4, 6))
            .Should().BeApproximately(body.DistanceTo(new Vector2(4, 6)), Eps);
    }

    [Fact]
    public void LaunchImpulse_AppliesPhysxForceImpulse2D()
    {
        var body = new FakeBody2D();
        body.LaunchImpulse(new Vector2(0, 2f), magnitude: 10f);
        body.ForceCallCount.Should().Be(1);
        body.LastForce!.Value.Type.Should().Be(PhysxForce.Kind.AddImpulse2D);
        body.LastForce!.Value.Vector.Y.Should().BeApproximately(10f, Eps);
    }

    [Fact]
    public void LaunchImpulse_ZeroMagnitudeIsNoOp()
    {
        var body = new FakeBody2D();
        body.LaunchImpulse(new Vector2(1, 0), magnitude: 0f);
        body.ForceCallCount.Should().Be(0);
    }

    [Fact]
    public void LaunchVelocity_AppliesSetLinearVelocity2D()
    {
        var body = new FakeBody2D();
        body.LaunchVelocity(new Vector2(1, 2));
        body.ForceCallCount.Should().Be(1);
        body.LastForce!.Value.Type.Should().Be(PhysxForce.Kind.SetLinearVelocity2D);
        body.LastForce!.Value.Vector.X.Should().BeApproximately(1f, Eps);
        body.LastForce!.Value.Vector.Y.Should().BeApproximately(2f, Eps);
    }
}
