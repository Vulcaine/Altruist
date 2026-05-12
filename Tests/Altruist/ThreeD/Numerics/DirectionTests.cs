using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class DirectionTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Calculate_NormalizedFullThreeD()
    {
        var dir = Direction.Calculate(new Vector3(0, 0, 0), new Vector3(3, 4, 12));
        dir.Length().Should().BeApproximately(1f, Eps);
        Vector3.Dot(dir, new Vector3(3, 4, 12)).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void Calculate_CoincidentReturnsZeroNotNaN()
    {
        var dir = Direction.Calculate(new Vector3(1, 2, 3), new Vector3(1, 2, 3));
        dir.Should().Be(Vector3.Zero);
        float.IsNaN(dir.X).Should().BeFalse();
    }

    [Fact]
    public void Horizontal_NormalizedAndYZero()
    {
        var dir = Direction.Horizontal(new Vector3(0, 5, 0), new Vector3(3, -2, 4));
        dir.Y.Should().Be(0f);
        dir.Length().Should().BeApproximately(1f, Eps);
        dir.X.Should().BeApproximately(0.6f, Eps);
        dir.Z.Should().BeApproximately(0.8f, Eps);
    }

    [Fact]
    public void Horizontal_CoincidentReturnsZero()
    {
        Direction.Horizontal(new Vector3(1, 2, 3), new Vector3(1, 99, 3))
            .Should().Be(Vector3.Zero);
    }

    [Fact]
    public void WithDistance_BothAtOnce()
    {
        var (dir, dist) = Direction.WithDistance(new Vector3(0, 0, 0), new Vector3(0, 0, 5));
        dist.Should().BeApproximately(5f, Eps);
        dir.Should().Be(new Vector3(0, 0, 1));
    }

    [Fact]
    public void WithDistance_CoincidentReturnsZeroes()
    {
        var (dir, dist) = Direction.WithDistance(Vector3.One, Vector3.One);
        dir.Should().Be(Vector3.Zero);
        dist.Should().Be(0f);
    }

    [Fact]
    public void PerpendicularXZ_Is90DegreesCcw()
    {
        // (1, _, 0) → (0, _, -1)
        var p = Direction.PerpendicularXZ(new Vector3(1, 0, 0));
        p.X.Should().BeApproximately(0f, Eps);
        p.Z.Should().BeApproximately(-1f, Eps);
        p.Y.Should().Be(0f);
    }

    [Fact]
    public void PerpendicularXZ_DotWithOriginalIsZero()
    {
        var v = new Vector3(0.6f, 0, 0.8f);
        var p = Direction.PerpendicularXZ(v);
        Vector3.Dot(v, p).Should().BeApproximately(0f, Eps);
    }
}
