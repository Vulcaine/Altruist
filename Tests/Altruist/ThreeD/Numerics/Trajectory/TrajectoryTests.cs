using System.Numerics;
using Altruist.ThreeD.Numerics.Trajectory;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics.Trajectory;

public class TrajectoryTests
{
    private const float Eps = 1e-3f;

    [Fact]
    public void ParabolicY_IsZeroAtTZeroAndOne()
    {
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicY(0f, 5f, 0.28f, 0.55f).Should().Be(0f);
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicY(1f, 5f, 0.28f, 0.55f).Should().Be(0f);
    }

    [Fact]
    public void ParabolicY_PeaksDuringHangPhase()
    {
        // At t = 0.4 (between rise=0.28 and hang=0.55), Y should equal peakHeight.
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicY(0.4f, 10f, 0.28f, 0.55f)
            .Should().BeApproximately(10f, Eps);
    }

    [Fact]
    public void ParabolicY_ZeroPeakIsFlat()
    {
        for (float t = 0f; t <= 1f; t += 0.1f)
            global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicY(t, 0f, 0.28f, 0.55f).Should().Be(0f);
    }

    [Fact]
    public void ParabolicSample_StartAndEndExact()
    {
        var s = new Vector3(1, 2, 3);
        var e = new Vector3(11, 2, 23);
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicSample(s, e, 0f, 5f, 0.28f, 0.55f).Should().Be(s);
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicSample(s, e, 1f, 5f, 0.28f, 0.55f).Should().Be(e);
    }

    [Fact]
    public void ParabolicPolyline_EndpointsMatch()
    {
        var s = new Vector3(0, 0, 0);
        var e = new Vector3(10, 0, 0);
        Span<Vector3> dest = stackalloc Vector3[8];
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicPolyline(s, e, peakHeight: 5f,
            riseEndN: 0.28f, hangEndN: 0.55f, dest);
        dest[0].Should().Be(s);
        dest[7].Should().Be(e);
    }

    [Fact]
    public void ParabolicPolyline_RisesThenFalls()
    {
        Span<Vector3> dest = stackalloc Vector3[11];
        global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicPolyline(
            new Vector3(0, 0, 0), new Vector3(10, 0, 0),
            peakHeight: 5f, riseEndN: 0.28f, hangEndN: 0.55f, dest);

        // Y values must rise from 0, peak somewhere in the middle, fall back to 0.
        dest[0].Y.Should().Be(0f);
        dest[10].Y.Should().Be(0f);
        var maxY = 0f;
        var maxIdx = -1;
        for (int i = 0; i < dest.Length; i++)
            if (dest[i].Y > maxY) { maxY = dest[i].Y; maxIdx = i; }
        maxIdx.Should().BeInRange(2, 8); // somewhere in the middle
        maxY.Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void ParabolicPolyline_RejectsTooSmallDest()
    {
        var act = () =>
        {
            Vector3[] dest = new Vector3[1];
            global::Altruist.ThreeD.Numerics.Trajectory.Trajectory.ParabolicPolyline(
                Vector3.Zero, Vector3.One, 1f, 0.28f, 0.55f, dest);
        };
        act.Should().Throw<ArgumentException>();
    }
}
