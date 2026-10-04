using System.Numerics;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;

namespace Tests.Altruist.Gaming.World.ThreeD;

/// <summary>Covers the Gaming-layer body extensions: IWorldObject3D sugar
/// overloads and the terrain-aware MoveToward / MoveTowardAngle paths.
/// Doesn't exercise LaunchAlong / LaunchTrajectory — those are 1-line
/// trampolines and the underlying runtimes have their own dedicated suites.</summary>
public class BodyNavigationExtensionsTests
{
    private const float Eps = 1e-3f;

    // ── IWorldObject3D sugar overloads ───────────────────────────────────

    [Fact]
    public void FaceToward_IWorldObject3D_SnapsRotation()
    {
        var body = new FakeBody { Position = Vector3.Zero };
        var target = new FakeWorldObject3D(new Vector3(5, 99, 0));
        body.FaceToward(target);
        body.GetYaw().Should().BeApproximately(MathF.PI / 2f, Eps);
    }

    [Fact]
    public void HorizontalDistanceTo_IWorldObject3D_IgnoresY()
    {
        var body = new FakeBody { Position = new Vector3(0, 100, 0) };
        var target = new FakeWorldObject3D(new Vector3(3, 0, 4));
        body.HorizontalDistanceTo(target).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void DistanceTo_IWorldObject3D_FullThreeD()
    {
        var body = new FakeBody { Position = Vector3.Zero };
        var target = new FakeWorldObject3D(new Vector3(3, 4, 12));
        body.DistanceTo(target).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void IsFacing_IWorldObject3D_InFront()
    {
        var body = new FakeBody { Rotation = Quaternion.Identity }; // facing +Z
        var target = new FakeWorldObject3D(new Vector3(0.1f, 0, 5f));
        body.IsFacing(target, halfAngleDegrees: 30f).Should().BeTrue();
    }

    [Fact]
    public void IsFacing_IWorldObject3D_Behind()
    {
        var body = new FakeBody { Rotation = Quaternion.Identity };
        var target = new FakeWorldObject3D(new Vector3(0, 0, -5f));
        body.IsFacing(target, halfAngleDegrees: 30f).Should().BeFalse();
    }

    [Fact]
    public void TurnToward_IWorldObject3D_StepsTowardTarget()
    {
        var body = new FakeBody { Position = Vector3.Zero, Rotation = Quaternion.Identity };
        var target = new FakeWorldObject3D(new Vector3(1, 0, 0)); // +X, yaw = π/2
        body.TurnToward(target, maxAngularSpeedRadPerSec: 1f, dt: 0.1f);
        body.GetYaw().Should().BeApproximately(0.1f, Eps);
    }

    [Fact]
    public void MoveToward_IWorldObject3D_WritesVelocityPreservingY()
    {
        var body = new FakeBody {
            Position = Vector3.Zero,
            LinearVelocity = new Vector3(0, -9.8f, 0),
        };
        var target = new FakeWorldObject3D(new Vector3(10, 0, 0));
        body.MoveToward(target, speed: 5f, dt: 0.1f);
        body.LinearVelocity.X.Should().BeApproximately(5f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, Eps);
    }

    // ── Terrain-aware MoveToward (position-write + Y-snap) ───────────────

    [Fact]
    public void MoveToward_Terrain_WritesPositionWithYSnap()
    {
        var body = new FakeBody { Position = new Vector3(0, 0, 0) };
        var terrain = new FakeTerrain(constantHeight: 42f);
        body.MoveToward(new Vector3(10, 0, 0), speed: 5f, dt: 0.1f, terrain);
        body.Position.X.Should().BeApproximately(0.5f, Eps); // step = speed*dt = 0.5
        body.Position.Y.Should().BeApproximately(42f, Eps); // snapped to terrain
        body.Position.Z.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void MoveToward_Terrain_Position3DOverloadMatchesVector3()
    {
        var bodyV = new FakeBody { Position = Vector3.Zero };
        var bodyP = new FakeBody { Position = Vector3.Zero };
        var terrain = new FakeTerrain(constantHeight: 7f);
        var v = new Vector3(10, 0, 0);
        bodyV.MoveToward(v, 5f, 0.1f, terrain);
        bodyP.MoveToward(Position3D.From(v), 5f, 0.1f, terrain);
        bodyP.Position.Should().Be(bodyV.Position);
    }

    [Fact]
    public void MoveToward_Terrain_IWorldObject3D_WritesPositionWithYSnap()
    {
        var body = new FakeBody { Position = Vector3.Zero };
        var terrain = new FakeTerrain(constantHeight: 13f);
        var target = new FakeWorldObject3D(new Vector3(10, 0, 0));
        body.MoveToward(target, speed: 5f, dt: 0.1f, terrain);
        body.Position.Y.Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void MoveToward_NullTerrain_FallsBackToVelocityWrite()
    {
        var body = new FakeBody {
            Position = Vector3.Zero,
            LinearVelocity = new Vector3(0, -9.8f, 0),
        };
        // Pass null terrain — should route to the velocity-based MoveToward.
        body.MoveToward(new Vector3(10, 0, 0), speed: 5f, dt: 0.1f, terrain: null!);
        body.Position.Should().Be(Vector3.Zero); // position unchanged
        body.LinearVelocity.X.Should().BeApproximately(5f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, Eps);
    }

    [Fact]
    public void MoveToward_Terrain_ZeroSpeedStops()
    {
        var body = new FakeBody {
            Position = new Vector3(1, 2, 3),
            LinearVelocity = new Vector3(4, -5, 6),
        };
        var terrain = new FakeTerrain(constantHeight: 0f);
        body.MoveToward(new Vector3(10, 0, 0), speed: 0f, dt: 0.1f, terrain);
        body.Position.Should().Be(new Vector3(1, 2, 3)); // unchanged
        body.LinearVelocity.Should().Be(new Vector3(0, -5, 0)); // Stop() preserves Y
    }

    [Fact]
    public void MoveTowardAngle_Terrain_WritesPositionWithYSnap()
    {
        var body = new FakeBody { Position = Vector3.Zero };
        var terrain = new FakeTerrain(constantHeight: 99f);
        // yaw 0 → +Z forward
        body.MoveTowardAngle(yawRadians: 0f, speed: 10f, dt: 0.1f, terrain);
        body.Position.X.Should().BeApproximately(0f, Eps);
        body.Position.Y.Should().BeApproximately(99f, Eps);
        body.Position.Z.Should().BeApproximately(1f, Eps); // step = speed*dt = 1.0
    }

    [Fact]
    public void MoveTowardAngle_NullTerrain_FallsBackToVelocityWrite()
    {
        var body = new FakeBody {
            Position = Vector3.Zero,
            LinearVelocity = new Vector3(0, -9.8f, 0),
        };
        body.MoveTowardAngle(yawRadians: 0f, speed: 5f, dt: 0.1f, terrain: null!);
        body.Position.Should().Be(Vector3.Zero);
        body.LinearVelocity.Z.Should().BeApproximately(5f, Eps);
        body.LinearVelocity.Y.Should().BeApproximately(-9.8f, Eps);
    }

    [Fact]
    public void MoveTowardAngle_Terrain_ZeroSpeedStops()
    {
        var body = new FakeBody { Position = new Vector3(1, 2, 3) };
        var terrain = new FakeTerrain(constantHeight: 0f);
        body.MoveTowardAngle(yawRadians: 0f, speed: 0f, dt: 0.1f, terrain);
        body.Position.Should().Be(new Vector3(1, 2, 3));
    }
}

// ── Fakes ────────────────────────────────────────────────────────────────

internal sealed class FakeWorldObject3D : AnonymousWorldObject3D
{
    public FakeWorldObject3D(Vector3 position)
        : base(new Transform3D(Position3D.From(position), Size3D.One, Scale3D.One, Rotation3D.Identity))
    {
    }
}

internal sealed class FakeTerrain : ITerrainProvider
{
    private readonly float _y;
    public FakeTerrain(float constantHeight) { _y = constantHeight; }
    public bool IsWalkable(float x, float y, float z) => true;
    public float GetHeight(float x, float z) => _y;
}
