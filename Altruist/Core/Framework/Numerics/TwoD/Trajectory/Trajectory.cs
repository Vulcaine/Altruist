/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics.Trajectory;

/// <summary>Pure parametric arc math — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Trajectory.Trajectory"/>. The arc
/// axis is Y (matching side-view 2D where Y is height); X is linear.
/// Tuning parameters (<paramref name="riseEndN"/>, <paramref name="hangEndN"/>)
/// are passed in — Metin2-specific defaults (0.28 / 0.55) live in Valeria.</summary>
public static class Trajectory2D
{
    /// <summary>Y offset above the linear lerp at parameter <paramref name="t"/>
    /// (0 = start, 1 = end). Three-phase: ease-out rise to <paramref name="riseEndN"/>,
    /// flat hang to <paramref name="hangEndN"/>, ease-in fall to 1.</summary>
    public static float ParabolicY(float t, float peakHeight, float riseEndN, float hangEndN)
    {
        if (peakHeight <= 0f) return 0f;
        if (t <= 0f || t >= 1f) return 0f;
        if (riseEndN <= 0f) riseEndN = 0.01f;
        if (hangEndN >= 1f) hangEndN = 0.99f;
        if (hangEndN < riseEndN) hangEndN = riseEndN;

        if (t < riseEndN)
        {
            var u = t / riseEndN;
            return peakHeight * (1f - (1f - u) * (1f - u));
        }
        if (t < hangEndN)
        {
            return peakHeight;
        }
        var v = (t - hangEndN) / (1f - hangEndN);
        return peakHeight * (1f - v * v);
    }

    /// <summary>Full 2D arc sample: lerps X from <paramref name="start"/> to
    /// <paramref name="end"/> and adds <see cref="ParabolicY"/> on top of the
    /// Y-lerp.</summary>
    public static Vector2 ParabolicSample(Vector2 start, Vector2 end, float t,
                                          float peakHeight, float riseEndN, float hangEndN)
    {
        var baseX = start.X + (end.X - start.X) * t;
        var baseY = start.Y + (end.Y - start.Y) * t;
        return new Vector2(baseX, baseY + ParabolicY(t, peakHeight, riseEndN, hangEndN));
    }

    /// <summary>Allocation-free polyline emission. Writes <paramref name="dest"/>.Length
    /// samples evenly spaced over t∈[0,1].</summary>
    public static void ParabolicPolyline(Vector2 start, Vector2 end, float peakHeight,
                                         float riseEndN, float hangEndN, Span<Vector2> dest)
    {
        if (dest.Length == 0) return;
        if (dest.Length == 1) { dest[0] = start; return; }
        var stepDen = dest.Length - 1;
        for (int i = 0; i < dest.Length; i++)
        {
            var t = (float)i / stepDen;
            dest[i] = ParabolicSample(start, end, t, peakHeight, riseEndN, hangEndN);
        }
    }
}
