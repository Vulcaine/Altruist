using System.Numerics;
using Altruist.TwoD.Numerics.Trajectory;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics.Trajectory;

public class Trajectory2DTests
{
    private const float Eps = 1e-3f;

    [Fact]
    public void ParabolicY_IsZeroAtTZeroAndOne()
    {
        Trajectory2D.ParabolicY(0f, 5f, 0.28f, 0.55f).Should().Be(0f);
        Trajectory2D.ParabolicY(1f, 5f, 0.28f, 0.55f).Should().Be(0f);
    }

    [Fact]
    public void ParabolicY_PeaksDuringHangPhase()
    {
        Trajectory2D.ParabolicY(0.4f, 10f, 0.28f, 0.55f).Should().BeApproximately(10f, Eps);
    }

    [Fact]
    public void ParabolicY_ZeroPeakIsFlat()
    {
        for (float t = 0f; t <= 1f; t += 0.1f)
            Trajectory2D.ParabolicY(t, 0f, 0.28f, 0.55f).Should().Be(0f);
    }

    [Fact]
    public void ParabolicSample_StartAndEndExact()
    {
        var s = new Vector2(1, 2);
        var e = new Vector2(11, 2);
        Trajectory2D.ParabolicSample(s, e, 0f, 5f, 0.28f, 0.55f).Should().Be(s);
        Trajectory2D.ParabolicSample(s, e, 1f, 5f, 0.28f, 0.55f).Should().Be(e);
    }

    [Fact]
    public void ParabolicPolyline_EndpointsMatch()
    {
        var s = new Vector2(0, 0);
        var e = new Vector2(10, 0);
        Span<Vector2> dest = stackalloc Vector2[8];
        Trajectory2D.ParabolicPolyline(s, e, peakHeight: 5f, riseEndN: 0.28f, hangEndN: 0.55f, dest);
        dest[0].Should().Be(s);
        dest[7].Should().Be(e);
    }

    [Fact]
    public void ParabolicPolyline_RisesThenFalls()
    {
        Span<Vector2> dest = stackalloc Vector2[11];
        Trajectory2D.ParabolicPolyline(new Vector2(0, 0), new Vector2(10, 0),
            peakHeight: 5f, riseEndN: 0.28f, hangEndN: 0.55f, dest);
        dest[0].Y.Should().Be(0f);
        dest[10].Y.Should().Be(0f);
        var maxY = 0f;
        for (int i = 0; i < dest.Length; i++)
            if (dest[i].Y > maxY) maxY = dest[i].Y;
        maxY.Should().BeApproximately(5f, Eps);
    }

    [Theory]
    [InlineData(0.1f, 0.28f, 0.55f)]
    [InlineData(0.4f, 0.28f, 0.55f)]
    [InlineData(0.8f, 0.28f, 0.55f)]
    [InlineData(0.5f, 0f, 1f)]
    [InlineData(0.3f, 0.6f, 0.2f)]
    [InlineData(-0.5f, 0.28f, 0.55f)]
    [InlineData(1.5f, 0.28f, 0.55f)]
    public void ParabolicSample_mirrors_the_3D_arc(float t, float riseEndN, float hangEndN)
    {
        var s2 = new Vector2(1f, 2f);
        var e2 = new Vector2(9f, -3f);
        var s3 = new Vector3(s2.X, s2.Y, 0f);
        var e3 = new Vector3(e2.X, e2.Y, 0f);

        var sample = Trajectory2D.ParabolicSample(s2, e2, t, 4f, riseEndN, hangEndN);
        var sample3 = global::Altruist.ThreeD.Numerics.Trajectory.Trajectory3D.ParabolicSample(s3, e3, t, 4f, riseEndN, hangEndN);

        sample.X.Should().Be(sample3.X);
        sample.Y.Should().Be(sample3.Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ParabolicPolyline_rejects_too_small_dest_like_3D(int length)
    {
        var act = () => Trajectory2D.ParabolicPolyline(Vector2.Zero, Vector2.One, 1f, 0.3f, 0.6f, new Vector2[length]);
        act.Should().Throw<ArgumentException>();
    }
}
