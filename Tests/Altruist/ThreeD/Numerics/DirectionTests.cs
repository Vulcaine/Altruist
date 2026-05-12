using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class DirectionTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Between_NormalizedFullThreeD()
    {
        var dir = Direction3D.Between(new Vector3(0, 0, 0), new Vector3(3, 4, 12));
        dir.Length().Should().BeApproximately(1f, Eps);
        Vector3.Dot(dir, new Vector3(3, 4, 12)).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void Between_CoincidentReturnsZeroNotNaN()
    {
        var dir = Direction3D.Between(new Vector3(1, 2, 3), new Vector3(1, 2, 3));
        dir.Should().Be(Vector3.Zero);
        float.IsNaN(dir.X).Should().BeFalse();
    }

    [Fact]
    public void Between_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 6, 7);
        Direction3D.Between(Position3D.From(a), Position3D.From(b))
            .Should().Be(Direction3D.Between(a, b));
    }

    [Fact]
    public void Toward_IsAliasOfBetween()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 6, 7);
        Direction3D.Toward(a, b).Should().Be(Direction3D.Between(a, b));
        Direction3D.Toward(Position3D.From(a), Position3D.From(b))
            .Should().Be(Direction3D.Between(Position3D.From(a), Position3D.From(b)));
    }

    [Fact]
    public void TowardAngle_MatchesYawToDirection()
    {
        // 90° yaw faces +X (sin 90° = 1, cos 90° = 0)
        var d = Direction3D.TowardAngle(MathF.PI / 2f);
        d.X.Should().BeApproximately(1f, Eps);
        d.Y.Should().Be(0f);
        d.Z.Should().BeApproximately(0f, Eps);
        d.Should().Be(Yaw3D.ToDirection(MathF.PI / 2f));
    }

    [Fact]
    public void Horizontal_NormalizedAndYZero()
    {
        var dir = Direction3D.Horizontal(new Vector3(0, 5, 0), new Vector3(3, -2, 4));
        dir.Y.Should().Be(0f);
        dir.Length().Should().BeApproximately(1f, Eps);
        dir.X.Should().BeApproximately(0.6f, Eps);
        dir.Z.Should().BeApproximately(0.8f, Eps);
    }

    [Fact]
    public void Horizontal_CoincidentReturnsZero()
    {
        Direction3D.Horizontal(new Vector3(1, 2, 3), new Vector3(1, 99, 3))
            .Should().Be(Vector3.Zero);
    }

    [Fact]
    public void Horizontal_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(0, 5, 0);
        var b = new Vector3(3, -2, 4);
        Direction3D.Horizontal(Position3D.From(a), Position3D.From(b))
            .Should().Be(Direction3D.Horizontal(a, b));
    }

    [Fact]
    public void WithDistance_BothAtOnce()
    {
        var (dir, dist) = Direction3D.WithDistance(new Vector3(0, 0, 0), new Vector3(0, 0, 5));
        dist.Should().BeApproximately(5f, Eps);
        dir.Should().Be(new Vector3(0, 0, 1));
    }

    [Fact]
    public void WithDistance_CoincidentReturnsZeroes()
    {
        var (dir, dist) = Direction3D.WithDistance(Vector3.One, Vector3.One);
        dir.Should().Be(Vector3.Zero);
        dist.Should().Be(0f);
    }

    [Fact]
    public void WithDistance_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 6, 7);
        var (dirP, distP) = Direction3D.WithDistance(Position3D.From(a), Position3D.From(b));
        var (dirV, distV) = Direction3D.WithDistance(a, b);
        dirP.Should().Be(dirV);
        distP.Should().BeApproximately(distV, Eps);
    }

    [Fact]
    public void PerpendicularXZ_Is90DegreesCcw()
    {
        // (1, _, 0) → (0, _, -1)
        var p = Direction3D.PerpendicularXZ(new Vector3(1, 0, 0));
        p.X.Should().BeApproximately(0f, Eps);
        p.Z.Should().BeApproximately(-1f, Eps);
        p.Y.Should().Be(0f);
    }

    [Fact]
    public void PerpendicularXZ_DotWithOriginalIsZero()
    {
        var v = new Vector3(0.6f, 0, 0.8f);
        var p = Direction3D.PerpendicularXZ(v);
        Vector3.Dot(v, p).Should().BeApproximately(0f, Eps);
    }
}
