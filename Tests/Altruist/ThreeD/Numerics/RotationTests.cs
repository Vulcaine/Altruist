using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class RotationTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Calculate_YawProducesRotationAroundY()
    {
        var q = Rotation.Calculate(MathF.PI / 2f); // 90° around Y
        // Forward (0,0,1) rotated by +π/2 around Y → (1, 0, 0).
        var fwd = Vector3.Transform(Vector3.UnitZ, q);
        fwd.X.Should().BeApproximately(1f, Eps);
        fwd.Y.Should().BeApproximately(0f, Eps);
        MathF.Abs(fwd.Z).Should().BeLessThan(Eps);
    }

    [Fact]
    public void Calculate_RoundTripThroughYaw()
    {
        var q = Rotation.Calculate(0.7f);
        Yaw.Calculate(q).Should().BeApproximately(0.7f, Eps);
    }

    [Fact]
    public void FromAxisAngle_NormalizesAxis()
    {
        var q1 = Rotation.FromAxisAngle(Vector3.UnitY, 1f);
        var q2 = Rotation.FromAxisAngle(new Vector3(0, 5, 0), 1f); // non-unit axis
        Yaw.Calculate(q1).Should().BeApproximately(Yaw.Calculate(q2), Eps);
    }

    [Fact]
    public void FromEuler_YawOnlyMatchesCalculate()
    {
        var qEuler = Rotation.FromEuler(0.5f, 0f, 0f);
        var qYaw = Rotation.Calculate(0.5f);
        Yaw.Calculate(qEuler).Should().BeApproximately(Yaw.Calculate(qYaw), Eps);
    }
}
