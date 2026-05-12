/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics.Trajectory;

/// <summary>Pure parametric trajectory math. The flagship shape is the
/// 3-phase parabolic arc (rise → hang → fall) used everywhere from
/// knockback flights to projectile arcs to summon dives. Constants are
/// always passed in; nothing about a specific game lives here.</summary>
public static class Trajectory
{
    /// <summary>Y offset above the linear XZ baseline at parameter <paramref name="t"/>
    /// (0 = start, 1 = end). Three-phase shape:
    /// <list type="bullet">
    /// <item>t ∈ [0, riseEndN]    → ease-out rise from 0 to <paramref name="peakHeight"/>.</item>
    /// <item>t ∈ [riseEndN, hangEndN] → flat hang at <paramref name="peakHeight"/>.</item>
    /// <item>t ∈ [hangEndN, 1]     → ease-in fall back to 0.</item>
    /// </list>
    /// Returns 0 when <paramref name="peakHeight"/> ≤ 0 (collapses to a flat lerp).</summary>
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
            return peakHeight * MathF.Sin(u * (MathF.PI * 0.5f));
        }
        if (t < hangEndN) return peakHeight;

        var fall = (t - hangEndN) / (1f - hangEndN);
        var s = MathF.Sin(fall * (MathF.PI * 0.5f));
        return peakHeight * (1f - s * s);
    }

    /// <summary>Sample a 3D arc at <paramref name="t"/>: lerp XZ from
    /// <paramref name="start"/> to <paramref name="end"/> and add the parabolic Y on top.</summary>
    public static Vector3 ParabolicSample(Vector3 start, Vector3 end, float t,
                                          float peakHeight, float riseEndN, float hangEndN)
    {
        if (t <= 0f) return start;
        if (t >= 1f) return end;
        var x = start.X + (end.X - start.X) * t;
        var z = start.Z + (end.Z - start.Z) * t;
        var y = start.Y + (end.Y - start.Y) * t + ParabolicY(t, peakHeight, riseEndN, hangEndN);
        return new Vector3(x, y, z);
    }

    /// <summary>Allocation-free polyline emission. Writes <paramref name="dest"/>.Length
    /// samples evenly spaced over t ∈ [0, 1] inclusive (so dest[0] = start,
    /// dest[^1] = end). Length must be ≥ 2.</summary>
    public static void ParabolicPolyline(Vector3 start, Vector3 end, float peakHeight,
                                         float riseEndN, float hangEndN, Span<Vector3> dest)
    {
        if (dest.Length < 2)
            throw new ArgumentException("dest must have at least 2 slots", nameof(dest));

        var step = 1f / (dest.Length - 1);
        for (int i = 0; i < dest.Length; i++)
            dest[i] = ParabolicSample(start, end, i * step, peakHeight, riseEndN, hangEndN);
    }
}
