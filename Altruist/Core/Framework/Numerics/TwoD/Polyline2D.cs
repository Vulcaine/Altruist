/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>Math layer. Building polylines (chain / loop collision shapes, paths) from lines and circular
/// arcs, and cleaning duplicate vertices out of them.
/// <para>Deterministic: same inputs give the same vertices (bits) on the same runtime; the
/// TypeScript twin produces the same vertex count and order.</para></summary>
public static class Polyline2D
{
    /// <summary>Appends a circular arc around (<paramref name="centerX"/>, <paramref name="centerY"/>)
    /// from <paramref name="startDegrees"/> to <paramref name="endDegrees"/> (standard convention:
    /// 0° = +X, counter-clockwise; going down is clockwise) as <c>steps + 1</c> vertices, both
    /// ends included, each segment covering at most <paramref name="maxSegmentDegrees"/>:
    /// <code>
    /// steps = Math.Max(minSegments, (int)MathF.Ceiling(MathF.Abs(end - start) / maxSegmentDegrees))
    /// a_i   = Angle.ToRadians(start + (end - start) * i / steps)
    /// p_i   = (centerX + radius * DeterministicMath.Cos(a_i), centerY + radius * DeterministicMath.Sin(a_i))
    /// </code></summary>
    public static void AppendArc(List<Vector2> points, float centerX, float centerY, float radius,
                                 float startDegrees, float endDegrees, float maxSegmentDegrees, int minSegments = 2)
    {
        float a0 = startDegrees, a1 = endDegrees;
        var steps = Math.Max(minSegments, (int)MathF.Ceiling(MathF.Abs(a1 - a0) / maxSegmentDegrees));
        for (var i = 0; i <= steps; i++)
        {
            var a = Angle.ToRadians(a0 + (a1 - a0) * i / steps);
            points.Add(new Vector2(centerX + radius * DeterministicMath.Cos(a), centerY + radius * DeterministicMath.Sin(a)));
        }
    }

    /// <summary>A copy without consecutive duplicates: a vertex is kept when its
    /// <see cref="Vector2.DistanceSquared"/> to the last kept one is above
    /// <paramref name="epsilonSquared"/>. When <paramref name="closed"/> and more than one vertex
    /// remains, a last vertex within <paramref name="epsilonSquared"/> of the first (squared distance
    /// below it) is dropped too, since a loop closes itself.</summary>
    public static List<Vector2> RemoveDuplicates(IReadOnlyList<Vector2> points, float epsilonSquared = 1e-10f, bool closed = true)
    {
        var outPts = new List<Vector2>(points.Count);
        foreach (var p in points)
        {
            if (outPts.Count == 0 || Vector2.DistanceSquared(outPts[^1], p) > epsilonSquared) outPts.Add(p);
        }
        if (closed && outPts.Count > 1 && Vector2.DistanceSquared(outPts[0], outPts[^1]) < epsilonSquared)
            outPts.RemoveAt(outPts.Count - 1);
        return outPts;
    }
}
