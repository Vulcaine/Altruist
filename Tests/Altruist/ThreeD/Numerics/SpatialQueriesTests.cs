using System.Numerics;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class SpatialQueriesTests
{
    [Fact]
    public void IsInRange_XZOnlyIgnoresY()
    {
        var origin = new Vector3(0, 0, 0);
        var target = new Vector3(3, 100, 4);
        SpatialQueries3D.IsInRange(origin, target, 6f, xzOnly: true).Should().BeTrue();
        SpatialQueries3D.IsInRange(origin, target, 6f, xzOnly: false).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_TargetInFrontIsHit()
    {
        // Facing +Z (yaw=0). Target slightly off-axis.
        SpatialQueries3D.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector3(0.5f, 0, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInCone_TargetBehindIsMiss()
    {
        SpatialQueries3D.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector3(0, 0, -5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_OutOfRangeIsMiss()
    {
        SpatialQueries3D.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(60f),
            range: 3f, target: new Vector3(0, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_ZeroDistanceIsHit()
    {
        SpatialQueries3D.IsInCone(new Vector3(1, 2, 3), yaw: 0f,
            halfAngleRadians: 0.1f, range: 5f, target: new Vector3(1, 2, 3)).Should().BeTrue();
    }

    [Fact]
    public void IsInLine_TargetAlongAxisIsHit()
    {
        // Yaw=0 → axis is +Z. Target on the axis at z=5.
        SpatialQueries3D.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0, 0, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInLine_TargetPastEndIsMiss()
    {
        SpatialQueries3D.IsInLine(Vector3.Zero, yaw: 0f, length: 4f, halfWidth: 1f,
            target: new Vector3(0, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetBehindIsMiss()
    {
        SpatialQueries3D.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0, 0, -1f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetOutsideHalfWidthIsMiss()
    {
        SpatialQueries3D.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 0.5f,
            target: new Vector3(2f, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetWithinHalfWidthIsHit()
    {
        SpatialQueries3D.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0.5f, 0, 5f)).Should().BeTrue();
    }

    // ── Position3D overload parity ────────────────────────────────────────

    [Fact]
    public void IsInRange_Position3DMatchesVector3()
    {
        var from = new Vector3(0, 0, 0);
        var to = new Vector3(3, 100, 4);
        SpatialQueries3D.IsInRange(Position3D.From(from), Position3D.From(to), 6f, xzOnly: true)
            .Should().Be(SpatialQueries3D.IsInRange(from, to, 6f, xzOnly: true));
        SpatialQueries3D.IsInRange(Position3D.From(from), Position3D.From(to), 6f, xzOnly: false)
            .Should().Be(SpatialQueries3D.IsInRange(from, to, 6f, xzOnly: false));
    }

    [Fact]
    public void IsInCone_Position3DMatchesVector3()
    {
        var origin = Vector3.Zero;
        var target = new Vector3(0.5f, 0, 5f);
        var half = Angle.ToRadians(30f);
        SpatialQueries3D.IsInCone(Position3D.From(origin), 0f, half, 10f, Position3D.From(target))
            .Should().Be(SpatialQueries3D.IsInCone(origin, 0f, half, 10f, target));
    }

    [Fact]
    public void IsInCone_Position3DTargetBehindMiss()
    {
        SpatialQueries3D.IsInCone(Position3D.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(30f), range: 10f,
            target: new Position3D(0f, 0f, -5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_Position3DMatchesVector3()
    {
        var origin = Vector3.Zero;
        var target = new Vector3(0.5f, 0, 5f);
        SpatialQueries3D.IsInLine(Position3D.From(origin), 0f, 10f, 1f, Position3D.From(target))
            .Should().Be(SpatialQueries3D.IsInLine(origin, 0f, 10f, 1f, target));
    }

    [Fact]
    public void IsInLine_Position3DOutsideHalfWidthMiss()
    {
        SpatialQueries3D.IsInLine(Position3D.Zero, yaw: 0f, length: 10f, halfWidth: 0.5f,
            target: new Position3D(2f, 0f, 5f)).Should().BeFalse();
    }
}
