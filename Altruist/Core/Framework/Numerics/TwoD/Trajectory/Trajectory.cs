/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics.Trajectory;

/// <summary>Pure parametric arc math — the exact 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Trajectory.Trajectory3D"/>: the same three-phase shape
/// (quarter-sine rise → hang → sine-squared fall), the same phase clamping and the same errors, so a 2D
/// sample equals the X/Y of the 3D sample for the same inputs. The arc axis is Y (side-view 2D, Y is
/// height); X is linear. Tuning parameters (<c>riseEndN</c>, <c>hangEndN</c>, as fractions of t) are always
/// passed in; no game-specific defaults live here. Uses <see cref="DeterministicMath"/>, so samples are
/// the same on every platform.</summary>
public static class Trajectory2D
{
    /// <summary>Y offset above the linear lerp at parameter <paramref name="t"/>
    /// (0 = start, 1 = end). Three-phase: quarter-sine ease-out rise to <paramref name="peakHeight"/>
    /// until <paramref name="riseEndN"/>, flat hang until <paramref name="hangEndN"/>, then
    /// <c>1 - sin²</c> ease-in fall to 0 at 1. Phase bounds are clamped to [1e-4, 1 - 1e-4] with
    /// <c>hangEndN ≥ riseEndN</c>. Returns 0 when <paramref name="peakHeight"/> ≤ 0 or t is outside (0, 1).</summary>
    public static float ParabolicY(float t, float peakHeight, float riseEndN, float hangEndN)
    {
        if (peakHeight <= 0f) return 0f;
        if (t <= 0f || t >= 1f) return 0f;
        if (riseEndN <= 0f) riseEndN = 1e-4f;
        if (hangEndN >= 1f) hangEndN = 1f - 1e-4f;
        if (hangEndN < riseEndN) hangEndN = riseEndN;

        if (t < riseEndN)
        {
            var u = t / riseEndN;
            return peakHeight * DeterministicMath.Sin(u * (MathF.PI * 0.5f));
        }
        if (t < hangEndN) return peakHeight;

        var fall = (t - hangEndN) / (1f - hangEndN);
        var s = DeterministicMath.Sin(fall * (MathF.PI * 0.5f));
        return peakHeight * (1f - s * s);
    }

    /// <summary>Full 2D arc sample: lerps X and Y from <paramref name="start"/> to
    /// <paramref name="end"/> and adds <see cref="ParabolicY"/> on top of the Y-lerp. Returns exactly
    /// <paramref name="start"/> for t ≤ 0 and <paramref name="end"/> for t ≥ 1.</summary>
    public static Vector2 ParabolicSample(Vector2 start, Vector2 end, float t,
                                          float peakHeight, float riseEndN, float hangEndN)
    {
        if (t <= 0f) return start;
        if (t >= 1f) return end;
        var x = start.X + (end.X - start.X) * t;
        var y = start.Y + (end.Y - start.Y) * t + ParabolicY(t, peakHeight, riseEndN, hangEndN);
        return new Vector2(x, y);
    }

    /// <summary>Allocation-free polyline emission. Writes <paramref name="dest"/>.Length
    /// samples evenly spaced over t ∈ [0, 1] inclusive (so dest[0] = start, dest[^1] = end).</summary>
    /// <exception cref="ArgumentException"><paramref name="dest"/> has fewer than 2 slots.</exception>
    public static void ParabolicPolyline(Vector2 start, Vector2 end, float peakHeight,
                                         float riseEndN, float hangEndN, Span<Vector2> dest)
    {
        if (dest.Length < 2)
            throw new ArgumentException("dest must have at least 2 slots", nameof(dest));

        var step = 1f / (dest.Length - 1);
        for (int i = 0; i < dest.Length; i++)
            dest[i] = ParabolicSample(start, end, i * step, peakHeight, riseEndN, hangEndN);
    }
}
