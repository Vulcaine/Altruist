using System.Numerics;
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
        SpatialQueries.IsInRange(origin, target, 6f, xzOnly: true).Should().BeTrue();
        SpatialQueries.IsInRange(origin, target, 6f, xzOnly: false).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_TargetInFrontIsHit()
    {
        // Facing +Z (yaw=0). Target slightly off-axis.
        SpatialQueries.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector3(0.5f, 0, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInCone_TargetBehindIsMiss()
    {
        SpatialQueries.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector3(0, 0, -5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_OutOfRangeIsMiss()
    {
        SpatialQueries.IsInCone(Vector3.Zero, yaw: 0f,
            halfAngleRadians: Angle.ToRadians(60f),
            range: 3f, target: new Vector3(0, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_ZeroDistanceIsHit()
    {
        SpatialQueries.IsInCone(new Vector3(1, 2, 3), yaw: 0f,
            halfAngleRadians: 0.1f, range: 5f, target: new Vector3(1, 2, 3)).Should().BeTrue();
    }

    [Fact]
    public void IsInLine_TargetAlongAxisIsHit()
    {
        // Yaw=0 → axis is +Z. Target on the axis at z=5.
        SpatialQueries.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0, 0, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInLine_TargetPastEndIsMiss()
    {
        SpatialQueries.IsInLine(Vector3.Zero, yaw: 0f, length: 4f, halfWidth: 1f,
            target: new Vector3(0, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetBehindIsMiss()
    {
        SpatialQueries.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0, 0, -1f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetOutsideHalfWidthIsMiss()
    {
        SpatialQueries.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 0.5f,
            target: new Vector3(2f, 0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetWithinHalfWidthIsHit()
    {
        SpatialQueries.IsInLine(Vector3.Zero, yaw: 0f, length: 10f, halfWidth: 1f,
            target: new Vector3(0.5f, 0, 5f)).Should().BeTrue();
    }
}
