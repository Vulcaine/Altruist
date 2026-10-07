using Altruist.Numerics;
using FluentAssertions;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Numerics;

/// <summary>Math layer, scalars: Scalar, Angle (wrap and rates), PiecewiseLinear,
/// DeterministicRandom. Every helper is compared bit for bit with the inline expression it
/// replaces.</summary>
public class MathLayerTests
{
    // ── Inline reference forms (what callers write today) ────────────────

    private static float InlineClamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    private static float InlineWrap(float a)
    {
        a = (a + MathF.PI) % (2 * MathF.PI);
        if (a < 0) a += 2 * MathF.PI;
        return a - MathF.PI;
    }

    private static float InlineApproach(float current, float target, float maxDelta) =>
        current < target ? MathF.Min(current + maxDelta, target) : MathF.Max(current - maxDelta, target);

    // ── Scalar ────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_Approach_Lerp_match_inline_bits()
    {
        var xs = Floats().ToArray();
        for (var i = 0; i + 2 < xs.Length; i++)
        {
            float a = xs[i], b = xs[i + 1], c = xs[i + 2];
            Same(Scalar.Clamp(a, -1, 1), InlineClamp(a, -1, 1));
            Same(Scalar.Clamp01(a), InlineClamp(a, 0, 1));
            Same(Scalar.Approach(a, b, MathF.Abs(c)), InlineApproach(a, b, MathF.Abs(c)));
            Same(Scalar.Lerp(a, b, c), a + (b - a) * c);
            Same(Scalar.InverseLerp(a, b, c), (c - a) / (b - a));
            Same(Scalar.Pow01(a, 1.7f), MathF.Pow(Math.Clamp(a, 0, 1), 1.7f));
            Same(Scalar.RoundHalfUp(a), MathF.Floor(a + 0.5f));
            Same(Scalar.Quantize(a / 40f), MathF.Round(InlineClamp(a / 40f, -1, 1) * 127) / 127f);
        }
    }

    [Fact]
    public void Approach_never_overshoots()
    {
        Scalar.Approach(0, 10, 3).Should().Be(3);
        Scalar.Approach(9, 10, 3).Should().Be(10);
        Scalar.Approach(10, 0, 4).Should().Be(6);
        Scalar.Approach(1, 0, 4).Should().Be(0);
        Scalar.Approach(5, 5, 1).Should().Be(5);
    }

    [Fact]
    public void Lerp_InverseLerp_Remap()
    {
        Scalar.Lerp(2, 6, 0.25f).Should().Be(3);
        Scalar.InverseLerp(2, 6, 3).Should().Be(0.25f);
        Scalar.Remap(5, 0, 10, 100, 200).Should().Be(150);
        Scalar.Clamp(float.NaN, 0, 1).Should().Be(float.NaN);
        Scalar.Clamp(5, 3, 1).Should().Be(1); // never throws (min > max)
    }

    [Fact]
    public void Sign_and_SignOr()
    {
        Scalar.Sign(2).Should().Be(1);
        Scalar.Sign(-0.1f).Should().Be(-1);
        Scalar.Sign(0).Should().Be(0);
        Scalar.Sign(float.NaN).Should().Be(0);
        Scalar.SignOr(3, -1).Should().Be(1);
        Scalar.SignOr(-3, 1).Should().Be(-1);
        Scalar.SignOr(0, -1).Should().Be(-1);
        Scalar.SignOr(-0f, 1).Should().Be(1);
        Scalar.SignOr(0).Should().Be(0);
    }

    [Fact]
    public void RoundHalfUp_rounds_ties_up_and_has_the_float_edge()
    {
        Scalar.RoundHalfUp(2.5f).Should().Be(3);
        Scalar.RoundHalfUp(-2.5f).Should().Be(-2);
        Scalar.RoundHalfUp(2.4f).Should().Be(2);
        Scalar.RoundHalfUp(0.49999997f).Should().Be(1); // documented float32 edge
    }

    [Fact]
    public void Quantize_snaps_to_signed_byte_steps_and_clamps()
    {
        Scalar.Quantize(2f).Should().Be(1f);
        Scalar.Quantize(-2f).Should().Be(-1f);
        Scalar.Quantize(0.5f).Should().Be(64 / 127f);
        Scalar.Quantize(0.5f, 2).Should().Be(0.5f);
    }

    // ── Angle ─────────────────────────────────────────────────────────────

    [Fact]
    public void Wrap_matches_inline_modulo_wrap_bits()
    {
        foreach (var a in Floats(5000, 100f)) Same(Angle.Wrap(a), InlineWrap(a));
    }

    [Fact]
    public void Wrap_range_and_pi_edges()
    {
        Angle.Wrap(MathF.PI).Should().Be(-MathF.PI);
        Angle.Wrap(-MathF.PI).Should().Be(-MathF.PI);
        Angle.Wrap(0).Should().Be(0);
        Angle.Wrap(3 * MathF.PI / 2).Should().BeApproximately(-MathF.PI / 2, 1e-5f);
        foreach (var a in Floats(2000, 1000f))
        {
            var w = Angle.Wrap(a);
            w.Should().BeGreaterThanOrEqualTo(-MathF.PI).And.BeLessThanOrEqualTo(MathF.PI);
        }
        // Normalize keeps +π: the two wraps are deliberately different helpers.
        Angle.Normalize(MathF.PI).Should().Be(MathF.PI);
    }

    [Fact]
    public void Angle_rate_helpers_match_inline_bits()
    {
        var xs = Floats(1500, 10f).ToArray();
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            float rot = xs[i], tgt = xs[i + 1];
            Same(Angle.WrappedDelta(rot, tgt), InlineWrap(tgt - rot));
            Same(Angle.NearestEquivalent(rot, tgt), rot + InlineWrap(tgt - rot));
            Same(Angle.NearestFullTurn(rot), rot - InlineWrap(rot));
            Same(Angle.RateToward(rot, tgt, 12f), InlineWrap(tgt - rot) * 12f);
            Same(Angle.ClampedRateToward(rot, tgt, 9f, 7f), InlineClamp(InlineWrap(tgt - rot) * 9f, -7f, 7f));
            Same(Angle.ArriveRate(rot, tgt, 0.004f, 1f / 60f), InlineWrap(tgt - rot) / MathF.Max(0.004f, 1f / 60f));
            Same(Angle.ArriveRate(rot, tgt, 0.3f, 1f / 60f), InlineWrap(tgt - rot) / MathF.Max(0.3f, 1f / 60f));
        }
    }

    [Fact]
    public void NearestEquivalent_and_NearestFullTurn_semantics()
    {
        Angle.NearestEquivalent(4 * MathF.PI, 0.1f).Should().BeApproximately(4 * MathF.PI + 0.1f, 1e-4f);
        Angle.NearestFullTurn(2 * MathF.PI + 0.2f).Should().BeApproximately(2 * MathF.PI, 1e-4f);
        Angle.NearestFullTurn(-0.3f).Should().BeApproximately(0f, 1e-6f);
        Angle.ClampedRateToward(0, 3, 10, 2).Should().Be(2);
        Angle.ArriveRate(0, 1, 0.5f, 1f / 60f).Should().BeApproximately(2, 1e-5f);
        Angle.ArriveRate(0, 1, 0f, 0.5f).Should().BeApproximately(2, 1e-5f);
    }

    // ── PiecewiseLinear ───────────────────────────────────────────────────

    private static readonly (float Value, float Skill)[] Table = { (0, 0.1f), (10, 0.4f), (25, 0.8f), (40, 1f) };
    private static readonly (float T, float H)[] Rise = { (0, 0), (0.167f, 2.6f), (0.333f, 4.5f), (0.5f, 5.4f), (0.6f, 5.6f) };

    private static float InlineCurve((float Value, float Skill)[] points, float value)
    {
        if (points.Length == 0) return 0;
        if (value <= points[0].Value) return points[0].Skill;
        for (var i = 1; i < points.Length; i++)
        {
            var (x1, y1) = points[i];
            if (value <= x1)
            {
                var (x0, y0) = points[i - 1];
                return y0 + (y1 - y0) * (value - x0) / (x1 - x0);
            }
        }
        return points[^1].Skill;
    }

    private static float InlineInverse((float T, float H)[] table, float h)
    {
        if (h <= 0) return 0;
        for (var i = 1; i < table.Length; i++)
        {
            var (t1, h1) = table[i];
            if (h <= h1)
            {
                var (t0, h0) = table[i - 1];
                return t0 + (t1 - t0) * (h - h0) / (h1 - h0);
            }
        }
        return float.PositiveInfinity;
    }

    [Fact]
    public void PiecewiseLinear_matches_inline_bits()
    {
        foreach (var x in Floats(3000, 60f))
        {
            Same(PiecewiseLinear.Evaluate(Table, x), InlineCurve(Table, x));
            Same(PiecewiseLinear.Inverse(Rise, x / 8f), InlineInverse(Rise, x / 8f));
        }
    }

    [Fact]
    public void PiecewiseLinear_ends_and_empty()
    {
        PiecewiseLinear.Evaluate(Table, -5).Should().Be(0.1f);
        PiecewiseLinear.Evaluate(Table, 100).Should().Be(1f);
        PiecewiseLinear.Evaluate(Table, 5).Should().BeApproximately(0.25f, 1e-6f);
        PiecewiseLinear.Evaluate(ReadOnlySpan<(float, float)>.Empty, 3).Should().Be(0);
        PiecewiseLinear.Inverse(Rise, 0).Should().Be(0);
        PiecewiseLinear.Inverse(Rise, 99).Should().Be(float.PositiveInfinity);
        PiecewiseLinear.Inverse(ReadOnlySpan<(float, float)>.Empty, 1).Should().Be(float.PositiveInfinity);
        PiecewiseLinear.Inverse(Rise, 1.3f).Should().BeApproximately(0.0835f, 1e-5f);
    }

    // ── DeterministicRandom (golden values shared with the TypeScript tests) ──

    [Theory]
    [InlineData(0, 625341585u, new uint[] { 3777279546, 2342155435, 1692513196, 1525286, 4071472047 })]
    [InlineData(1, 3144846624u, new uint[] { 2953441579, 1270223261, 3107675839, 2406887145, 533617724 })]
    [InlineData(42, 3495691163u, new uint[] { 1439054348, 3262749359, 3568059673, 3722848478, 121729184 })]
    [InlineData(-7, 2302564536u, new uint[] { 117853228, 519489229, 63946394, 2005669396, 1553169107 })]
    [InlineData(int.MaxValue, 3297604318u, new uint[] { 805728533, 984706791, 4208799601, 3109502440, 2208842045 })]
    public void Random_sequence_is_golden(int seed, uint state, uint[] sequence)
    {
        var r = new DeterministicRandom(seed);
        r.State.Should().Be(state);
        foreach (var expected in sequence) r.NextUInt().Should().Be(expected);
    }

    [Fact]
    public void Random_derived_draws_are_golden()
    {
        var r = new DeterministicRandom(42);
        r.Next().Should().Be(0.3350559500977397);
        r.Signed().Should().Be((float)0.5193360666744411);
        r.Gaussian().Should().Be((float)0.45177824376150966);
        r.Jitter(3).Should().Be(-3);
        r.Chance(0.5).Should().BeFalse();
    }

    [Fact]
    public void Random_zero_state_falls_back_and_state_round_trips()
    {
        new DeterministicRandom(1539571937).State.Should().Be(0x1234567u);
        var a = new DeterministicRandom(9);
        a.NextUInt();
        var b = DeterministicRandom.FromState(a.State);
        for (var i = 0; i < 100; i++) b.NextUInt().Should().Be(a.NextUInt());
        b.State = 0;
        b.State.Should().Be(0x1234567u);
    }

    [Fact]
    public void Random_ranges()
    {
        var r = new DeterministicRandom(5);
        for (var i = 0; i < 10000; i++)
        {
            r.Next().Should().BeInRange(0, 1).And.NotBe(1);
            r.Signed().Should().BeInRange(-1, 1);
            r.Gaussian().Should().BeInRange(-3, 3);
            r.Jitter(2).Should().BeInRange(-2, 2);
        }
        var before = r.State;
        r.Jitter(0).Should().Be(0);
        r.State.Should().Be(before, "Jitter(0) does not draw");
        r.Chance(1).Should().BeTrue();
        r.Chance(0).Should().BeFalse();
    }
}
