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

    /// <summary>The index of the point of <paramref name="points"/> nearest <paramref name="point"/> and
    /// its distance, searching <paramref name="window"/> indices either side of <paramref name="last"/>
    /// (so a line that crosses itself never jumps to the other part), wrapping round a
    /// <paramref name="closed"/> line (its last point repeats its first), or every point when
    /// <paramref name="last"/> is negative. Ties keep the lower searched index. Mirrors TS
    /// <c>nearestIndex</c>.</summary>
    public static (int Index, float Distance) NearestIndex(IReadOnlyList<Vector2> points, Vector2 point, int last, int window, bool closed)
    {
        var all = last < 0;
        var n = closed ? points.Count - 1 : points.Count;
        var lo = all ? 0 : closed ? last - window : Math.Max(0, last - window);
        var hi = all ? points.Count - 1 : closed ? last + window : Math.Min(points.Count - 1, last + window);
        var best = Math.Max(0, last);
        var bestD = float.PositiveInfinity;
        for (var k = lo; k <= hi; k++)
        {
            var i = (k % n + n) % n;
            var d = Vector2.DistanceSquared(points[i], point);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return (best, MathF.Sqrt(bestD));
    }

    /// <summary>Drops the points that lie on the straight line between their neighbours (within
    /// <paramref name="tolerance"/> of the line from the last kept point to the next point, and between
    /// the two), so a straight run is one segment; the first and last points are kept. Mirrors TS
    /// <c>dropCollinear</c>.</summary>
    public static List<Vector2> DropCollinear(IReadOnlyList<Vector2> points, float tolerance)
    {
        if (points.Count < 3) return points.ToList();
        var output = new List<Vector2> { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var a = output[^1];
            var b = points[i];
            var u = points[i + 1] - a;
            var len2 = u.LengthSquared();
            var along = Vector2.Dot(b - a, u);
            var off = MathF.Abs(VectorMath2D.Cross(b - a, u));
            if (len2 == 0 || along < 0 || along > len2 || off > tolerance * MathF.Sqrt(len2)) output.Add(b);
        }
        output.Add(points[^1]);
        return output;
    }
}
