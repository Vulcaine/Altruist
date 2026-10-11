/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Numerics;

/// <summary>Math layer. Piecewise-linear curves given as (X, Y) points sorted by X — tuning tables,
/// response curves, "time to reach a height" tables.
/// <para>Deterministic: same inputs give the same bits on the same runtime. Each segment is
/// evaluated as <c>y0 + (y1 - y0) * (x - x0) / (x1 - x0)</c>, in that order.</para></summary>
public static class PiecewiseLinear
{
    /// <summary>Y at <paramref name="x"/>, clamped at both ends: the first point's Y at or before
    /// its X, the last point's Y past its X, 0 for an empty curve. The first segment whose end
    /// X is ≥ <paramref name="x"/> is used.</summary>
    public static float Evaluate(ReadOnlySpan<(float X, float Y)> points, float x)
    {
        if (points.Length == 0) return 0;
        if (x <= points[0].X) return points[0].Y;
        for (var i = 1; i < points.Length; i++)
        {
            var (x1, y1) = points[i];
            if (x <= x1)
            {
                var (x0, y0) = points[i - 1];
                return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
            }
        }
        return points[^1].Y;
    }

    /// <summary>The inverse lookup on a curve whose Y increases: the X at which the curve reaches
    /// <paramref name="y"/>. At or below the first point's Y it is the first point's X; above the
    /// last point's Y it is <see cref="float.PositiveInfinity"/> ("never reached"), as it is for an
    /// empty curve. The first segment whose end Y is ≥ <paramref name="y"/> is used:
    /// <c>x0 + (x1 - x0) * (y - y0) / (y1 - y0)</c>.</summary>
    public static float Inverse(ReadOnlySpan<(float X, float Y)> points, float y)
    {
        if (points.Length == 0) return float.PositiveInfinity;
        if (y <= points[0].Y) return points[0].X;
        for (var i = 1; i < points.Length; i++)
        {
            var (x1, y1) = points[i];
            if (y <= y1)
            {
                var (x0, y0) = points[i - 1];
                return x0 + (x1 - x0) * (y - y0) / (y1 - y0);
            }
        }
        return float.PositiveInfinity;
    }
}
