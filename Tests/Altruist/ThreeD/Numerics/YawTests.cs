using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class YawTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void FromDirection_PlusZIsZero() =>
        Yaw.FromDirection(0f, 1f).Should().BeApproximately(0f, Eps);

    [Fact]
    public void FromDirection_PlusXIsHalfPi() =>
        Yaw.FromDirection(1f, 0f).Should().BeApproximately(MathF.PI / 2f, Eps);

    [Fact]
    public void FromDirection_MinusZIsPi() =>
        MathF.Abs(Yaw.FromDirection(0f, -1f)).Should().BeApproximately(MathF.PI, Eps);

    [Fact]
    public void FromDirection_MinusXIsMinusHalfPi() =>
        Yaw.FromDirection(-1f, 0f).Should().BeApproximately(-MathF.PI / 2f, Eps);

    [Fact]
    public void FromDirection_NearZero_ReturnsZeroNotNaN()
    {
        var y = Yaw.FromDirection(0f, 0f);
        float.IsNaN(y).Should().BeFalse();
        y.Should().Be(0f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1.234f)]
    [InlineData(-1.234f)]
    [InlineData(MathF.PI - 0.01f)]
    public void RoundTrip_YawToQuaternionToYaw(float yaw)
    {
        var q = Rotation.Calculate(yaw);
        Yaw.Calculate(q).Should().BeApproximately(yaw, Eps);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-1.0f)]
    public void RoundTrip_YawToDirectionToYaw(float yaw)
    {
        var dir = Yaw.ToDirection(yaw);
        dir.Y.Should().Be(0f);
        Yaw.FromVector(dir).Should().BeApproximately(yaw, Eps);
    }
}
