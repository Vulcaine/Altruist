/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>"Stupid Funnel" path smoothing (Mikko Mononen's algorithm).
/// Given the polygon-level path returned by A*, walk the portal sequence
/// between consecutive polys and produce a list of waypoints that hug the
/// inside corners — yielding visually natural diagonal paths instead of
/// the centroid-to-centroid zig-zag the raw A* result implies.
///
/// Operates on the XZ plane; Y is sampled from the polygon containing each
/// emitted waypoint so paths follow terrain elevation.</summary>
public static class NavMeshFunnel
{
    /// <summary>Smooth a polygon-level path into a world-space waypoint
    /// list. <paramref name="start"/> and <paramref name="end"/> are the
    /// actual start/end positions; the polygons are the A* result.</summary>
    /// <param name="graph">The nav-mesh the polygons belong to.</param>
    /// <param name="polyPath">Polygon sequence from <see cref="NavMeshPathfinder.TryFind"/>.</param>
    /// <param name="start">Start position (first waypoint).</param>
    /// <param name="end">End position (last waypoint).</param>
    /// <returns>A new waypoint list beginning with <paramref name="start"/> and ending with <paramref name="end"/>.</returns>
    public static List<Vector3> Smooth(NavMeshGraph graph, int[] polyPath, Vector3 start, Vector3 end)
    {
        var output = new List<Vector3>(polyPath.Length + 2);
        output.Add(start);

        if (polyPath.Length <= 1)
        {
            output.Add(end);
            return output;
        }

        // Build the portal sequence: portals[i] is the shared edge between
        // polyPath[i] and polyPath[i+1]. Last "portal" is degenerate at end.
        int portalCount = polyPath.Length;
        var leftPortals = new Vector3[portalCount];
        var rightPortals = new Vector3[portalCount];

        // Initial "portal" at the start point — both sides collapse to it.
        leftPortals[0] = start;
        rightPortals[0] = start;

        for (int i = 1; i < polyPath.Length; i++)
        {
            if (!graph.TryGetPortal(polyPath[i - 1], polyPath[i], out var portal))
            {
                // Adjacency missing — bail to a centroid fallback to avoid
                // emitting nonsense waypoints. Should never happen for a
                // path that A* actually found.
                output.Add(graph.GetPoly(polyPath[i]).Centroid);
                continue;
            }
            // Edge.V0 is on the LEFT when crossing from poly[i-1] to poly[i]
            // (NavMeshBuilder enforces this orientation invariant).
            leftPortals[i] = portal.V0;
            rightPortals[i] = portal.V1;
        }

        // Final "portal" at the end point — both sides collapse to it.
        // We extend the arrays implicitly by short-circuiting in the loop.

        Vector3 portalApex = start;
        Vector3 portalLeft = start;
        Vector3 portalRight = start;
        int apexIdx = 0;
        int leftIdx = 0;
        int rightIdx = 0;

        // Iterate through portals; index `i` represents portal[i].
        // Sentinel iteration at portalCount = the end point as a degenerate portal.
        for (int i = 1; i <= portalCount; i++)
        {
            Vector3 left = i < portalCount ? leftPortals[i] : end;
            Vector3 right = i < portalCount ? rightPortals[i] : end;

            // Update RIGHT side of the funnel.
            if (TriArea2(portalApex, portalRight, right) <= 0f)
            {
                if (Approx(portalApex, portalRight) || TriArea2(portalApex, portalLeft, right) > 0f)
                {
                    // Tighten the funnel.
                    portalRight = right;
                    rightIdx = i;
                }
                else
                {
                    // Right crossed left — emit the left vertex as a corner
                    // and restart the funnel from there.
                    output.Add(SnapToMesh(graph, portalLeft));
                    portalApex = portalLeft;
                    apexIdx = leftIdx;

                    portalLeft = portalApex;
                    portalRight = portalApex;
                    leftIdx = apexIdx;
                    rightIdx = apexIdx;

                    // Restart scan from the apex.
                    i = apexIdx;
                    continue;
                }
            }

            // Update LEFT side of the funnel.
            if (TriArea2(portalApex, portalLeft, left) >= 0f)
            {
                if (Approx(portalApex, portalLeft) || TriArea2(portalApex, portalRight, left) < 0f)
                {
                    portalLeft = left;
                    leftIdx = i;
                }
                else
                {
                    output.Add(SnapToMesh(graph, portalRight));
                    portalApex = portalRight;
                    apexIdx = rightIdx;

                    portalLeft = portalApex;
                    portalRight = portalApex;
                    leftIdx = apexIdx;
                    rightIdx = apexIdx;

                    i = apexIdx;
                    continue;
                }
            }
        }

        // Always emit the final endpoint — the funnel may have already added
        // it as a corner, in which case skip the duplicate.
        if (output.Count == 0 || !Approx(output[^1], end))
            output.Add(end);

        return output;
    }

    /// <summary>2× signed area of triangle ABC on the XZ plane. Sign tells
    /// us which side of segment AB point C is on — positive = left.</summary>
    private static float TriArea2(Vector3 a, Vector3 b, Vector3 c)
    {
        float ax = b.X - a.X;
        float az = b.Z - a.Z;
        float bx = c.X - a.X;
        float bz = c.Z - a.Z;
        return bx * az - ax * bz;
    }

    private static bool Approx(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz < 1e-6f;
    }

    /// <summary>Snap an emitted corner to the nav-mesh so its Y reflects
    /// the polygon at that XZ — without this corners keep the portal-edge
    /// Y, which can be 1+ meters off when the two adjacent rectangles sit
    /// on a slope.</summary>
    private static Vector3 SnapToMesh(NavMeshGraph graph, Vector3 corner)
    {
        if (graph.TryLocate(corner, out int polyIdx))
        {
            var poly = graph.GetPoly(polyIdx);
            return new Vector3(corner.X, poly.SampleYAt(corner.X, corner.Z), corner.Z);
        }
        return corner;
    }
}
