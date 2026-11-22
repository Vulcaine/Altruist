using System.Numerics;
using Altruist.Gaming.Combat;
using Altruist.Gaming.Combat.TwoD;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.Gaming.Combat.TwoD;

public class SweepQuery2DTests
{
    [Fact]
    public void Cone_StoresMathAngleAndFullDegrees()
    {
        var q = SweepQuery2D.Cone(new Vector2(1, 2), rotation: 0.5f, range: 7f,
                                  halfAngleRadians: Angle.ToRadians(30f));

        q.Type.Should().Be(SweepType.Cone);
        q.CenterX.Should().Be(1f);
        q.CenterY.Should().Be(2f);
        q.Range.Should().Be(7f);
        q.Direction.Should().BeApproximately(MathF.PI * 0.5f - 0.5f, 1e-4f);
        q.Angle.Should().BeApproximately(60f, 1e-3f);
    }

    [Fact]
    public void Cone_Position2DMatchesVector2()
    {
        var v = new Vector2(1, 2);
        var qV = SweepQuery2D.Cone(v, 0.5f, 7f, 0.4f);
        var qP = SweepQuery2D.Cone(Position2D.Of(1, 2), 0.5f, 7f, 0.4f);
        qP.Should().Be(qV);
    }

    [Fact]
    public void Line_StoresHalfWidth()
    {
        var q = SweepQuery2D.Line(Vector2.Zero, rotation: 0f, length: 5f, halfWidth: 0.75f);
        q.Type.Should().Be(SweepType.Line);
        q.Range.Should().Be(5f);
        q.Direction.Should().BeApproximately(MathF.PI * 0.5f, 1e-4f);
        q.Width.Should().Be(0.75f);
    }

    [Fact]
    public void Line_Position2DMatchesVector2()
    {
        var v = new Vector2(1, 2);
        var qV = SweepQuery2D.Line(v, 0.5f, 5f, 0.75f);
        var qP = SweepQuery2D.Line(Position2D.Of(1, 2), 0.5f, 5f, 0.75f);
        qP.Should().Be(qV);
    }

    [Fact]
    public void Sphere_StoresRangeAsRadius()
    {
        var q = SweepQuery2D.Sphere(new Vector2(10, 10), radius: 4f);
        q.Type.Should().Be(SweepType.Sphere);
        q.CenterX.Should().Be(10f);
        q.CenterY.Should().Be(10f);
        q.Range.Should().Be(4f);
    }

    [Fact]
    public void Sphere_Position2DMatchesVector2()
    {
        var v = new Vector2(10, 10);
        var qV = SweepQuery2D.Sphere(v, 4f);
        var qP = SweepQuery2D.Sphere(Position2D.Of(10, 10), 4f);
        qP.Should().Be(qV);
    }
}
