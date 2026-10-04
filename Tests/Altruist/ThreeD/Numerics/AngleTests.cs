using Altruist.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class AngleTests
{
    private const float Eps = 1e-4f;

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(MathF.PI * 2f + 0.5f, 0.5f)]
    [InlineData(-MathF.PI * 2f - 0.5f, -0.5f)]
    [InlineData(MathF.PI + 0.1f, -MathF.PI + 0.1f)]
    public void Normalize_WrapsToPlusMinusPi(float input, float expected) =>
        Angle.Normalize(input).Should().BeApproximately(expected, Eps);

    [Theory]
    [InlineData(MathF.PI)]
    [InlineData(-MathF.PI)]
    public void Normalize_PiBoundaryStaysInRange(float input)
    {
        var r = Angle.Normalize(input);
        MathF.Abs(r).Should().BeApproximately(MathF.PI, Eps);
    }

    [Fact]
    public void ShortestDifference_PicksShortWayAroundWrap()
    {
        var d = Angle.ShortestDifference(-3f, 3f);
        MathF.Abs(d).Should().BeLessThan(0.4f);
    }

    [Fact]
    public void ShortestDifference_SignMatchesRotationDirection()
    {
        Angle.ShortestDifference(0f, 0.5f).Should().BeGreaterThan(0f);
        Angle.ShortestDifference(0f, -0.5f).Should().BeLessThan(0f);
    }

    [Fact]
    public void MoveToward_StepsTowardTargetByMaxDelta() =>
        Angle.MoveToward(0f, 1f, 0.3f).Should().BeApproximately(0.3f, Eps);

    [Fact]
    public void MoveToward_NeverOvershoots() =>
        Angle.MoveToward(0f, 0.2f, 1f).Should().BeApproximately(0.2f, Eps);

    [Fact]
    public void MoveToward_TakesShortWayAcrossWrap()
    {
        // |shortest diff(-3, 3)| ≈ 0.28; max step 0.1 → step backward to -3.1.
        Angle.MoveToward(-3f, 3f, 0.1f).Should().BeApproximately(-3.1f, Eps);
    }

    [Fact]
    public void MoveToward_LargeStepReachesTargetWhenInsideMaxDelta() =>
        Angle.MoveToward(-3f, 3f, 0.5f).Should().BeApproximately(3f, Eps);

    [Fact]
    public void MoveToward_ZeroMaxDeltaReturnsCurrent() =>
        Angle.MoveToward(1f, 2f, 0f).Should().Be(1f);

    [Fact]
    public void DegreesRadians_RoundTrip()
    {
        var rad = Angle.ToRadians(180f);
        rad.Should().BeApproximately(MathF.PI, Eps);
        Angle.ToDegrees(rad).Should().BeApproximately(180f, Eps);
    }
}
