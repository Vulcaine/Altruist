/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Numerics;

/// <summary>"Angle bookkeeping" — wrap, signed difference, clamped step,
/// degree↔radian conversion, nearest equivalent angle, angular rates toward a target.
/// Pure functions over radians. Dimension-agnostic (used identically by 2D and 3D
/// consumers), hence the neutral namespace.</summary>
public static class Angle
{
    private const float TwoPi = MathF.PI * 2f;

    /// <summary>Wrap <paramref name="radians"/> into [-π, π].</summary>
    public static float Normalize(float radians)
    {
        radians = MathF.IEEERemainder(radians, TwoPi);
        if (radians > MathF.PI) radians -= TwoPi;
        else if (radians < -MathF.PI) radians += TwoPi;
        return radians;
    }

    /// <summary>Signed shortest delta from <paramref name="from"/> to
    /// <paramref name="to"/>. Positive = rotate in the +yaw direction.</summary>
    public static float ShortestDifference(float from, float to)
        => Normalize(to - from);

    /// <summary>Step <paramref name="current"/> toward <paramref name="target"/>
    /// by at most <paramref name="maxDelta"/> radians, taking the short way
    /// around. Never overshoots.</summary>
    public static float MoveToward(float current, float target, float maxDelta)
    {
        if (maxDelta <= 0f) return current;
        var diff = ShortestDifference(current, target);
        if (MathF.Abs(diff) <= maxDelta) return target;
        return Normalize(current + MathF.Sign(diff) * maxDelta);
    }

    /// <summary>Converts <paramref name="degrees"/> to radians (no wrapping; combine with
    /// <see cref="Normalize"/> if a [-π, π] result is needed).</summary>
    public static float ToRadians(float degrees) => degrees * (MathF.PI / 180f);
    /// <summary>Converts <paramref name="radians"/> to degrees (no wrapping).</summary>
    public static float ToDegrees(float radians) => radians * (180f / MathF.PI);

    // ── Modulo wrap and rates ─────────────────────────────────────────────
    //
    // Deterministic: same inputs give the same bits on the same runtime. Each helper evaluates
    // exactly the expression in its summary, so it can replace that expression written inline.

    /// <summary>Wraps <paramref name="radians"/> by modulo:
    /// <c>a = (radians + π) % 2π; if (a &lt; 0) a += 2π; return a - π</c>. The result lies in
    /// [-π, π): an exact +π maps to -π (where <see cref="Normalize"/>, an IEEE remainder, keeps +π),
    /// and other inputs can differ from <see cref="Normalize"/> in the last bit. Use this one when
    /// the arithmetic has to match <c>(a + PI) % (2 * PI)</c> code bit for bit (the TypeScript twin
    /// <c>wrap</c> computes the same expression on JavaScript numbers); use
    /// <see cref="Normalize"/> otherwise.</summary>
    public static float Wrap(float radians)
    {
        var a = (radians + MathF.PI) % TwoPi;
        if (a < 0) a += TwoPi;
        return a - MathF.PI;
    }

    /// <summary>Signed delta from <paramref name="from"/> to <paramref name="to"/> wrapped with
    /// <see cref="Wrap"/>: <c>Wrap(to - from)</c>.</summary>
    public static float WrappedDelta(float from, float to) => Wrap(to - from);

    /// <summary>The angle equivalent to <paramref name="angle"/> (same direction, a multiple of 2π
    /// apart) nearest to <paramref name="reference"/>: <c>reference + Wrap(angle - reference)</c>.
    /// Snapping a body to it never spins it a full turn.</summary>
    public static float NearestEquivalent(float reference, float angle) => reference + Wrap(angle - reference);

    /// <summary>The multiple of 2π nearest to <paramref name="radians"/> ("level after the spin"):
    /// <c>radians - Wrap(radians)</c>.</summary>
    public static float NearestFullTurn(float radians) => radians - Wrap(radians);

    /// <summary>Proportional angular rate toward <paramref name="target"/>:
    /// <c>Wrap(target - current) * gain</c> (rad/s for a gain in 1/s).</summary>
    public static float RateToward(float current, float target, float gain) => Wrap(target - current) * gain;

    /// <summary><see cref="RateToward"/> clamped to ±<paramref name="maxRate"/>:
    /// <c>Scalar.Clamp(Wrap(target - current) * gain, -maxRate, maxRate)</c>.</summary>
    public static float ClampedRateToward(float current, float target, float gain, float maxRate) =>
        Scalar.Clamp(Wrap(target - current) * gain, -maxRate, maxRate);

    /// <summary>The constant angular rate that arrives at <paramref name="target"/> in
    /// <paramref name="time"/> seconds (never faster than in <paramref name="minTime"/>, e.g. one
    /// step): <c>Wrap(target - current) / MathF.Max(time, minTime)</c>.</summary>
    public static float ArriveRate(float current, float target, float time, float minTime) =>
        Wrap(target - current) / MathF.Max(time, minTime);

    /// <summary>The constant angular rate of <paramref name="direction"/> (±1) full turns in
    /// <paramref name="time"/> seconds (a timed spin or flip): <c>direction * MathF.PI * 2 / time</c>.</summary>
    public static float FullTurnRate(float time, int direction) => direction * MathF.PI * 2 / time;

    // ── Sectors (double precision) ────────────────────────────────────────
    //
    // For bucketing directions into equal pie slices (cells, coverage maps, discrete aim). Double
    // precision, as such code usually is.

    /// <summary>Which of <paramref name="sectors"/> equal slices the direction
    /// (<paramref name="dx"/>, <paramref name="dy"/>) falls in, counting counter-clockwise from -X
    /// (slice 0 starts at -π): <c>(int)Math.Floor((DeterministicMath.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * sectors) % sectors</c>.
    /// Exactly +π (pointing along -X from above) wraps to slice 0.</summary>
    public static int SectorToward(double dx, double dy, int sectors) =>
        (int)Math.Floor((DeterministicMath.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * sectors) % sectors;

    /// <summary>The <see cref="SectorToward"/> slice of the direction at <paramref name="radians"/>
    /// (any angle, wrapped through its sine and cosine):
    /// <c>(int)Math.Floor((DeterministicMath.Atan2(DeterministicMath.Sin(radians), DeterministicMath.Cos(radians)) + Math.PI) / (2 * Math.PI) * sectors) % sectors</c>.</summary>
    public static int Sector(double radians, int sectors) => SectorToward(DeterministicMath.Cos(radians), DeterministicMath.Sin(radians), sectors);
}
