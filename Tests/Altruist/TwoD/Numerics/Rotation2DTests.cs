using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class Rotation2DTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void FromRadians_StoresValueExactly()
    {
        Rotation2D.FromRadians(0.7f).Radians.Should().BeApproximately(0.7f, Eps);
    }

    [Fact]
    public void FromDegrees_Converts()
    {
        Rotation2D.FromDegrees(180f).Radians.Should().BeApproximately(MathF.PI, Eps);
    }

    [Fact]
    public void ToDegrees_Converts()
    {
        Rotation2D.FromRadians(MathF.PI / 2f).ToDegrees().Should().BeApproximately(90f, Eps);
    }

    [Fact]
    public void Zero_IsZeroRadians()
    {
        Rotation2D.Zero.Radians.Should().Be(0f);
    }
}
