using System.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class Yaw2DTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void FromDirection_PlusYIsZero() =>
        Yaw2D.FromDirection(0f, 1f).Should().BeApproximately(0f, Eps);

    [Fact]
    public void FromDirection_PlusXIsHalfPi() =>
        Yaw2D.FromDirection(1f, 0f).Should().BeApproximately(MathF.PI / 2f, Eps);

    [Fact]
    public void FromDirection_MinusYIsPi() =>
        MathF.Abs(Yaw2D.FromDirection(0f, -1f)).Should().BeApproximately(MathF.PI, Eps);

    [Fact]
    public void FromDirection_NearZero_ReturnsZeroNotNaN()
    {
        var y = Yaw2D.FromDirection(0f, 0f);
        float.IsNaN(y).Should().BeFalse();
        y.Should().Be(0f);
    }

    [Fact]
    public void FromDirection_Position2DOverloadMatchesScalar()
    {
        var from = new Vector2(1, 2);
        var to = new Vector2(4, 6);
        Yaw2D.FromDirection(Position2D.Of(1, 2), Position2D.Of(4, 6))
            .Should().BeApproximately(Yaw2D.FromDirection(to.X - from.X, to.Y - from.Y), Eps);
    }

    [Fact]
    public void Calculate_ExtractsFromRotation2D()
    {
        var rot = Rotation2D.FromRadians(0.7f);
        Yaw2D.Calculate(rot).Should().BeApproximately(0.7f, Eps);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-1.0f)]
    public void RoundTrip_YawToDirectionToYaw(float yaw)
    {
        var dir = Yaw2D.ToDirection(yaw);
        Yaw2D.FromVector(dir).Should().BeApproximately(yaw, Eps);
    }
}
