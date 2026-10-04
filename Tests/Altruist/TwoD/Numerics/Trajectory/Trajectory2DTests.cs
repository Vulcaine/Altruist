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
}
