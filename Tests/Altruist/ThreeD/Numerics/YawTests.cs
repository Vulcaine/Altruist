using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class YawTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void FromDirection_PlusZIsZero() =>
        Yaw3D.FromDirection(0f, 1f).Should().BeApproximately(0f, Eps);

    [Fact]
    public void FromDirection_PlusXIsHalfPi() =>
        Yaw3D.FromDirection(1f, 0f).Should().BeApproximately(MathF.PI / 2f, Eps);

    [Fact]
    public void FromDirection_MinusZIsPi() =>
        MathF.Abs(Yaw3D.FromDirection(0f, -1f)).Should().BeApproximately(MathF.PI, Eps);

    [Fact]
    public void FromDirection_MinusXIsMinusHalfPi() =>
        Yaw3D.FromDirection(-1f, 0f).Should().BeApproximately(-MathF.PI / 2f, Eps);

    [Fact]
    public void FromDirection_NearZero_ReturnsZeroNotNaN()
    {
        var y = Yaw3D.FromDirection(0f, 0f);
        float.IsNaN(y).Should().BeFalse();
        y.Should().Be(0f);
    }

    [Fact]
    public void FromDirection_Position3DOverloadMatchesScalar()
    {
        var from = new Vector3(1, 2, 3);
        var to = new Vector3(4, 6, 7);
        Yaw3D.FromDirection(Position3D.From(from), Position3D.From(to))
            .Should().BeApproximately(Yaw3D.FromDirection(to.X - from.X, to.Z - from.Z), Eps);
    }

    [Fact]
    public void FromDirection_Position3D_PlusZIsZero()
        => Yaw3D.FromDirection(Position3D.Zero, new Position3D(0f, 0f, 1f))
            .Should().BeApproximately(0f, Eps);

    [Fact]
    public void FromDirection_Position3D_PlusXIsHalfPi()
        => Yaw3D.FromDirection(Position3D.Zero, new Position3D(1f, 0f, 0f))
            .Should().BeApproximately(MathF.PI / 2f, Eps);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1.234f)]
    [InlineData(-1.234f)]
    [InlineData(MathF.PI - 0.01f)]
    public void RoundTrip_YawToQuaternionToYaw(float yaw)
    {
        var q = Rotation3D.FromYaw(yaw).ToQuaternion();
        Yaw3D.Calculate(q).Should().BeApproximately(yaw, Eps);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-1.0f)]
    public void RoundTrip_YawToDirectionToYaw(float yaw)
    {
        var dir = Yaw3D.ToDirection(yaw);
        dir.Y.Should().Be(0f);
        Yaw3D.FromVector(dir).Should().BeApproximately(yaw, Eps);
    }
}
