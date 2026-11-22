using System.Numerics;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class SpatialQueries2DTests
{
    [Fact]
    public void IsInRange_WithinRangeIsHit()
    {
        SpatialQueries2D.IsInRange(Vector2.Zero, new Vector2(3, 4), 6f).Should().BeTrue();
    }

    [Fact]
    public void IsInRange_OutsideRangeIsMiss()
    {
        SpatialQueries2D.IsInRange(Vector2.Zero, new Vector2(3, 4), 4f).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_TargetInFrontIsHit()
    {
        // rotation=0 → axis is +Y (Atan2(dx, dy) convention).
        SpatialQueries2D.IsInCone(Vector2.Zero, rotationRadians: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector2(0.5f, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInCone_TargetBehindIsMiss()
    {
        SpatialQueries2D.IsInCone(Vector2.Zero, rotationRadians: 0f,
            halfAngleRadians: Angle.ToRadians(30f),
            range: 10f, target: new Vector2(0f, -5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInCone_OutOfRangeIsMiss()
    {
        SpatialQueries2D.IsInCone(Vector2.Zero, rotationRadians: 0f,
            halfAngleRadians: Angle.ToRadians(60f),
            range: 3f, target: new Vector2(0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetAlongAxisIsHit()
    {
        SpatialQueries2D.IsInLine(Vector2.Zero, rotationRadians: 0f, length: 10f, halfWidth: 1f,
            target: new Vector2(0, 5f)).Should().BeTrue();
    }

    [Fact]
    public void IsInLine_TargetPastEndIsMiss()
    {
        SpatialQueries2D.IsInLine(Vector2.Zero, rotationRadians: 0f, length: 4f, halfWidth: 1f,
            target: new Vector2(0, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInLine_TargetOutsideHalfWidthIsMiss()
    {
        SpatialQueries2D.IsInLine(Vector2.Zero, rotationRadians: 0f, length: 10f, halfWidth: 0.5f,
            target: new Vector2(2f, 5f)).Should().BeFalse();
    }

    [Fact]
    public void IsInRange_Position2DMatchesVector2()
    {
        SpatialQueries2D.IsInRange(Position2D.Zero, Position2D.Of(3, 4), 6f)
            .Should().Be(SpatialQueries2D.IsInRange(Vector2.Zero, new Vector2(3, 4), 6f));
    }

    [Fact]
    public void IsInCone_Position2DMatchesVector2()
    {
        SpatialQueries2D.IsInCone(Position2D.Zero, 0f, Angle.ToRadians(30f), 10f, Position2D.Of(0, 5))
            .Should().Be(SpatialQueries2D.IsInCone(Vector2.Zero, 0f, Angle.ToRadians(30f), 10f, new Vector2(0, 5)));
    }

    [Fact]
    public void IsInLine_Position2DMatchesVector2()
    {
        SpatialQueries2D.IsInLine(Position2D.Zero, 0f, 10f, 1f, Position2D.Of(0, 5))
            .Should().Be(SpatialQueries2D.IsInLine(Vector2.Zero, 0f, 10f, 1f, new Vector2(0, 5)));
    }
}
