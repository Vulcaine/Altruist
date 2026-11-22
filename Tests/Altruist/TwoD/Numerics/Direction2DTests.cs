using System.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class Direction2DTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Between_Normalized()
    {
        var dir = Direction2D.Between(new Vector2(0, 0), new Vector2(3, 4));
        dir.Length().Should().BeApproximately(1f, Eps);
        dir.X.Should().BeApproximately(0.6f, Eps);
        dir.Y.Should().BeApproximately(0.8f, Eps);
    }

    [Fact]
    public void Between_CoincidentReturnsZeroNotNaN()
    {
        var d = Direction2D.Between(Vector2.One, Vector2.One);
        d.Should().Be(Vector2.Zero);
        float.IsNaN(d.X).Should().BeFalse();
    }

    [Fact]
    public void Toward_IsAliasOfBetween()
    {
        var a = new Vector2(1, 2);
        var b = new Vector2(4, 6);
        Direction2D.Toward(a, b).Should().Be(Direction2D.Between(a, b));
    }

    [Fact]
    public void TowardAngle_MatchesYaw2DToDirection()
    {
        // Convention: rotation=0 → +Y (matches 3D's "forward = +Z")
        var d0 = Direction2D.TowardAngle(0f);
        d0.X.Should().BeApproximately(0f, Eps);
        d0.Y.Should().BeApproximately(1f, Eps);
        d0.Should().Be(Yaw2D.ToDirection(0f));

        var d90 = Direction2D.TowardAngle(MathF.PI / 2f);
        d90.X.Should().BeApproximately(1f, Eps);
        d90.Y.Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void WithDistance_BothAtOnce()
    {
        var (dir, dist) = Direction2D.WithDistance(new Vector2(0, 0), new Vector2(0, 5));
        dist.Should().BeApproximately(5f, Eps);
        dir.Should().Be(new Vector2(0, 1));
    }

    [Fact]
    public void WithDistance_CoincidentReturnsZeroes()
    {
        var (dir, dist) = Direction2D.WithDistance(Vector2.One, Vector2.One);
        dir.Should().Be(Vector2.Zero);
        dist.Should().Be(0f);
    }

    [Fact]
    public void Perpendicular_Is90DegreesCcw()
    {
        var p = Direction2D.Perpendicular(new Vector2(1, 0));
        p.X.Should().BeApproximately(0f, Eps);
        p.Y.Should().BeApproximately(1f, Eps);
    }

    [Fact]
    public void Perpendicular_DotWithOriginalIsZero()
    {
        var v = new Vector2(0.6f, 0.8f);
        var p = Direction2D.Perpendicular(v);
        Vector2.Dot(v, p).Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void Between_Position2DOverloadMatchesVector2()
    {
        var a = new Vector2(1, 2);
        var b = new Vector2(4, 6);
        Direction2D.Between(Position2D.Of(1, 2), Position2D.Of(4, 6))
            .Should().Be(Direction2D.Between(a, b));
    }
}
