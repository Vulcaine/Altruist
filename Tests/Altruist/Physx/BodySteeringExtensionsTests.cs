using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Physx;

public class BodySteeringExtensionsTests
{
    private const float Eps = 1e-3f;

    [Fact]
    public void FaceToward_SnapsRotationToYawTowardTarget()
    {
        var body = new FakeBody { Position = new Vector3(0, 0, 0) };
        body.FaceToward(new Vector3(5f, 99f, 0f)); // +X — yaw should be π/2
        body.GetYaw().Should().BeApproximately(MathF.PI / 2f, Eps);
    }

    [Fact]
    public void FaceToward_Position3DOverloadMatchesVector3()
    {
        var bodyV = new FakeBody { Position = new Vector3(0, 0, 0) };
        var bodyP = new FakeBody { Position = new Vector3(0, 0, 0) };
        var v = new Vector3(5f, 99f, 0f);
        bodyV.FaceToward(v);
        bodyP.FaceToward(Position3D.From(v));
        bodyP.Rotation.Should().Be(bodyV.Rotation);
    }

    [Fact]
    public void FaceToward_NoOpOnCoincidentTarget()
    {
        var body = new FakeBody { Position = new Vector3(1, 2, 3), Rotation = Quaternion.Identity };
        body.FaceToward(new Vector3(1, 99, 3));
        body.Rotation.Should().Be(Quaternion.Identity);
    }

    [Fact]
    public void TurnToward_ClampsAtMaxAngularSpeed()
    {
        var body = new FakeBody { Position = Vector3.Zero, Rotation = Quaternion.Identity };
        // Target is +X (yaw = π/2). Max step = 1.0 rad/s × 0.1s = 0.1 rad.
        body.TurnToward(new Vector3(1, 0, 0), maxAngularSpeedRadPerSec: 1f, dt: 0.1f);
        body.GetYaw().Should().BeApproximately(0.1f, Eps);
    }

    [Fact]
    public void TurnToward_NeverOvershoots()
    {
        var body = new FakeBody { Position = Vector3.Zero, Rotation = Quaternion.Identity };
        // Target +X (yaw = π/2). One huge step — should land exactly at π/2.
        body.TurnToward(new Vector3(1, 0, 0), maxAngularSpeedRadPerSec: 100f, dt: 1f);
        body.GetYaw().Should().BeApproximately(MathF.PI / 2f, Eps);
    }

    [Fact]
    public void MoveToward_PreservesYVelocity()
    {
        var body = new FakeBody {
            Position = Vector3.Zero,
            LinearVelocity = new Vector3(0, -9.8f, 0)
        };
        body.MoveToward(new Vector3(10, 0, 0), speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(5f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, Eps);
        body.LinearVelocity.Z.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void MoveToward_ZeroSpeedStops()
    {
        var body = new FakeBody {
            LinearVelocity = new Vector3(3, -2, 4)
        };
        body.MoveToward(new Vector3(10, 0, 10), speed: 0f, dt: 0.1f);
        body.LinearVelocity.X.Should().Be(0f);
        body.LinearVelocity.Y.Should().BeApproximately(-2f, Eps); // Y preserved
        body.LinearVelocity.Z.Should().Be(0f);
    }

    [Fact]
    public void MoveToward_Position3DOverloadMatchesVector3()
    {
        var bodyV = new FakeBody { Position = Vector3.Zero, LinearVelocity = new Vector3(0, -9.8f, 0) };
        var bodyP = new FakeBody { Position = Vector3.Zero, LinearVelocity = new Vector3(0, -9.8f, 0) };
        var v = new Vector3(10, 0, 0);
        bodyV.MoveToward(v, speed: 5f, dt: 0.1f);
        bodyP.MoveToward(Position3D.From(v), speed: 5f, dt: 0.1f);
        bodyP.LinearVelocity.Should().Be(bodyV.LinearVelocity);
    }

    [Fact]
    public void MoveTowardAngle_StepsInGivenYaw()
    {
        var body = new FakeBody {
            Position = Vector3.Zero,
            LinearVelocity = new Vector3(0, -9.8f, 0)
        };
        // yaw = 0 → forward is +Z
        body.MoveTowardAngle(yawRadians: 0f, speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(0f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, Eps); // Y preserved
        body.LinearVelocity.Z.Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void MoveTowardAngle_ZeroSpeedStops()
    {
        var body = new FakeBody { LinearVelocity = new Vector3(3, -2, 4) };
        body.MoveTowardAngle(yawRadians: 0f, speed: 0f, dt: 0.1f);
        body.LinearVelocity.X.Should().Be(0f);
        body.LinearVelocity.Y.Should().BeApproximately(-2f, Eps);
        body.LinearVelocity.Z.Should().Be(0f);
    }

    [Fact]
    public void Stop_ZeroesXzKeepsY()
    {
        var body = new FakeBody { LinearVelocity = new Vector3(3, -7, 5) };
        body.Stop();
        body.LinearVelocity.Should().Be(new Vector3(0, -7, 0));
    }

    [Fact]
    public void IsFacing_TargetInFront()
    {
        var body = new FakeBody { Rotation = Quaternion.Identity }; // facing +Z
        body.IsFacing(new Vector3(0.1f, 0, 5f), halfAngleDegrees: 30f).Should().BeTrue();
    }

    [Fact]
    public void IsFacing_TargetBehind()
    {
        var body = new FakeBody { Rotation = Quaternion.Identity };
        body.IsFacing(new Vector3(0, 0, -5f), halfAngleDegrees: 30f).Should().BeFalse();
    }

    [Fact]
    public void HorizontalDistanceTo_IgnoresY()
    {
        var body = new FakeBody { Position = new Vector3(0, 100, 0) };
        body.HorizontalDistanceTo(new Vector3(3, 0, 4)).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void HorizontalDistanceTo_Position3DOverloadMatchesVector3()
    {
        var body = new FakeBody { Position = new Vector3(1, 100, 2) };
        var target = new Vector3(4, 0, 6);
        body.HorizontalDistanceTo(Position3D.From(target))
            .Should().BeApproximately(body.HorizontalDistanceTo(target), Eps);
    }

    [Fact]
    public void IsFacing_Position3DOverloadMatchesVector3()
    {
        var body = new FakeBody { Rotation = Quaternion.Identity };
        var target = new Vector3(0.1f, 0, 5f);
        body.IsFacing(Position3D.From(target), 30f)
            .Should().Be(body.IsFacing(target, 30f));
    }

    [Fact]
    public void TurnToward_Position3DOverloadMatchesVector3()
    {
        var bodyV = new FakeBody { Position = Vector3.Zero, Rotation = Quaternion.Identity };
        var bodyP = new FakeBody { Position = Vector3.Zero, Rotation = Quaternion.Identity };
        var target = new Vector3(1, 0, 0);
        bodyV.TurnToward(target, 1f, 0.1f);
        bodyP.TurnToward(Position3D.From(target), 1f, 0.1f);
        bodyP.GetYaw().Should().BeApproximately(bodyV.GetYaw(), Eps);
    }

    [Fact]
    public void DistanceTo_Full3D()
    {
        var body = new FakeBody { Position = new Vector3(0, 0, 0) };
        body.DistanceTo(new Vector3(3, 4, 12)).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void DistanceTo_Position3DOverloadMatchesVector3()
    {
        var body = new FakeBody { Position = new Vector3(1, 2, 3) };
        var target = new Vector3(4, 6, 7);
        body.DistanceTo(Position3D.From(target))
            .Should().BeApproximately(body.DistanceTo(target), Eps);
    }

    [Fact]
    public void LaunchImpulse_AppliesPhysxForceImpulse3D()
    {
        var body = new FakeBody();
        body.LaunchImpulse(new Vector3(0, 0, 2f), magnitude: 10f); // direction +Z, magnitude 10
        body.ForceCallCount.Should().Be(1);
        body.LastForce!.Value.Type.Should().Be(PhysxForce.Kind.AddImpulse3D);
        body.LastForce!.Value.Vector.Z.Should().BeApproximately(10f, Eps);
        body.LastForce!.Value.Vector.X.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void LaunchImpulse_ZeroMagnitudeIsNoOp()
    {
        var body = new FakeBody();
        body.LaunchImpulse(new Vector3(1, 0, 0), magnitude: 0f);
        body.ForceCallCount.Should().Be(0);
    }

    [Fact]
    public void LaunchImpulse_ZeroDirectionIsNoOp()
    {
        var body = new FakeBody();
        body.LaunchImpulse(Vector3.Zero, magnitude: 100f);
        body.ForceCallCount.Should().Be(0);
    }

    [Fact]
    public void LaunchVelocity_AppliesSetLinearVelocity3D()
    {
        var body = new FakeBody();
        body.LaunchVelocity(new Vector3(1, 2, 3));
        body.ForceCallCount.Should().Be(1);
        body.LastForce!.Value.Type.Should().Be(PhysxForce.Kind.SetLinearVelocity3D);
        body.LastForce!.Value.Vector.Should().Be(new Vector3(1, 2, 3));
    }
}
