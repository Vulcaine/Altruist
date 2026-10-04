using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Gaming.TwoD;

public class BodyNavigationExtensions2DTests
{
    private const float Eps = 1e-3f;

    [Fact]
    public void FaceToward_IWorldObject2D_SnapsRotation()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        var target = new FakeWorldObject2D(new Vector2(5f, 0f));
        body.FaceToward(target);
        body.GetRotation().Should().BeApproximately(MathF.PI / 2f, Eps);
    }

    [Fact]
    public void DistanceTo_IWorldObject2D_FullDistance()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        var target = new FakeWorldObject2D(new Vector2(3, 4));
        body.DistanceTo(target).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void IsFacing_IWorldObject2D_InFront()
    {
        var body = new FakeBody2D { RotationZ = 0f };
        var target = new FakeWorldObject2D(new Vector2(0.1f, 5f));
        body.IsFacing(target, halfAngleDegrees: 30f).Should().BeTrue();
    }

    [Fact]
    public void TurnToward_IWorldObject2D_StepsTowardTarget()
    {
        var body = new FakeBody2D { Position = Vector2.Zero, RotationZ = 0f };
        var target = new FakeWorldObject2D(new Vector2(1, 0));
        body.TurnToward(target, maxAngularSpeedRadPerSec: 1f, dt: 0.1f);
        body.RotationZ.Should().BeApproximately(0.1f, Eps);
    }

    [Fact]
    public void MoveToward_IWorldObject2D_WritesVelocity()
    {
        var body = new FakeBody2D { Position = Vector2.Zero };
        var target = new FakeWorldObject2D(new Vector2(10, 0));
        body.MoveToward(target, speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(5f, Eps);
    }
}

internal sealed class FakeWorldObject2D : AnonymousWorldObject2D
{
    public FakeWorldObject2D(Vector2 position)
        : base(new Transform2D(
            Position2D.Of((int)MathF.Round(position.X), (int)MathF.Round(position.Y)),
            Size2D.One, Scale2D.One, Rotation2D.Zero))
    {
    }
}
