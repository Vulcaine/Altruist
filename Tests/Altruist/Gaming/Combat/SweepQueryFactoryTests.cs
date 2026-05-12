using System.Numerics;
using Altruist.Gaming.Combat;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.Gaming.Combat;

public class SweepQueryFactoryTests
{
    [Fact]
    public void Cone_SetsPlanarXzAndConvertsHalfAngleToFullDegrees()
    {
        var q = SweepQueryFactory.Cone(new Vector3(1, 2, 3), yaw: 0.5f, range: 7f,
                                       halfAngleRadians: Angle.ToRadians(30f));

        q.Type.Should().Be(SweepType.Cone);
        q.Space.Should().Be(SweepSpace.PlanarXZ);
        q.CenterX.Should().Be(1f);
        q.CenterY.Should().Be(2f);
        q.CenterZ.Should().Be(3f);
        q.Range.Should().Be(7f);
        // Stored direction is math-angle = π/2 − yaw (CombatService convention).
        q.Direction.Should().BeApproximately(MathF.PI * 0.5f - 0.5f, 1e-4f);
        q.Angle.Should().BeApproximately(60f, 1e-3f); // full angle in degrees
    }

    [Fact]
    public void Line_SetsPlanarXzAndHalfWidth()
    {
        var q = SweepQueryFactory.Line(new Vector3(0, 0, 0), yaw: 0f, length: 5f, halfWidth: 0.75f);

        q.Type.Should().Be(SweepType.Line);
        q.Space.Should().Be(SweepSpace.PlanarXZ);
        q.Range.Should().Be(5f);
        // yaw=0 → math-angle = π/2.
        q.Direction.Should().BeApproximately(MathF.PI * 0.5f, 1e-4f);
        q.Width.Should().Be(0.75f);
    }

    [Fact]
    public void Sphere_SetsPlanarXzAndRange()
    {
        var q = SweepQueryFactory.Sphere(new Vector3(10, 0, 10), radius: 4f);

        q.Type.Should().Be(SweepType.Sphere);
        q.Space.Should().Be(SweepSpace.PlanarXZ);
        q.CenterX.Should().Be(10f);
        q.CenterZ.Should().Be(10f);
        q.Range.Should().Be(4f);
    }
}
