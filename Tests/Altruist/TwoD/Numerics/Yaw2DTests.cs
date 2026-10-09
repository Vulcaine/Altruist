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

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(-2.5f)]
    [InlineData(4f)]
    public void Calculate_returns_the_yaw_of_the_rotated_local_up_axis(float radians)
    {
        var yaw = Yaw2D.Calculate(Rotation2D.FromRadians(radians));

        var facing = Yaw2D.ToDirection(yaw);
        var up = Rotation2D.UpAt(radians);
        facing.X.Should().BeApproximately(up.X, Eps);
        facing.Y.Should().BeApproximately(up.Y, Eps);
        MathF.Abs(yaw).Should().BeLessThanOrEqualTo(MathF.PI);
    }

    [Fact]
    public void Calculate_quarter_turn_counter_clockwise_faces_minus_x() =>
        Yaw2D.Calculate(Rotation2D.FromRadians(MathF.PI / 2f)).Should().BeApproximately(-MathF.PI / 2f, Eps);

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
