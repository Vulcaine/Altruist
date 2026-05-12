using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class RotationTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void FromYaw_ProducesRotationAroundY()
    {
        var q = Rotation3D.FromYaw(MathF.PI / 2f).ToQuaternion(); // 90° around Y
        // Forward (0,0,1) rotated by +π/2 around Y → (1, 0, 0).
        var fwd = Vector3.Transform(Vector3.UnitZ, q);
        fwd.X.Should().BeApproximately(1f, Eps);
        fwd.Y.Should().BeApproximately(0f, Eps);
        MathF.Abs(fwd.Z).Should().BeLessThan(Eps);
    }

    [Fact]
    public void FromYaw_RoundTripThroughYaw3D()
    {
        var q = Rotation3D.FromYaw(0.7f).ToQuaternion();
        Yaw3D.Calculate(q).Should().BeApproximately(0.7f, Eps);
    }

    [Fact]
    public void FromAxisAngle_NormalizesAxis()
    {
        var q1 = Rotation3D.FromAxisAngle(Vector3.UnitY, 1f).ToQuaternion();
        var q2 = Rotation3D.FromAxisAngle(new Vector3(0, 5, 0), 1f).ToQuaternion(); // non-unit axis
        Yaw3D.Calculate(q1).Should().BeApproximately(Yaw3D.Calculate(q2), Eps);
    }

    [Fact]
    public void FromEulerRadians_YawOnlyMatchesFromYaw()
    {
        var qEuler = Rotation3D.FromEulerRadians(0.5f, 0f, 0f).ToQuaternion();
        var qYaw = Rotation3D.FromYaw(0.5f).ToQuaternion();
        Yaw3D.Calculate(qEuler).Should().BeApproximately(Yaw3D.Calculate(qYaw), Eps);
    }
}
