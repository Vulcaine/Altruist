/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>A convex polygon on the nav-mesh. Vertices are stored CCW when
/// viewed from above (looking down -Y). For greedy-meshed grid cells these
/// are quads; future authoring/loaders may produce arbitrary convex polys.
/// The Y coordinate of each vertex is sampled from the heightmap at build
/// time so paths follow terrain elevation.</summary>
public sealed class NavPoly
{
    /// <summary>Index of this polygon in its <see cref="NavMeshGraph"/>.</summary>
    public int Index { get; }
    /// <summary>Vertices in world space, CCW from above.</summary>
    public Vector3[] Vertices { get; }
    /// <summary>Vertex average (used for A* costs and heuristics).</summary>
    public Vector3 Centroid { get; }
    /// <summary>Axis-aligned XZ bounds, used for spatial-index pruning: minimum X (in <c>X</c>) and Z (in <c>Y</c>).</summary>
    public Vector2 MinXZ { get; }
    /// <summary>Maximum X (in <c>X</c>) and Z (in <c>Y</c>) of the polygon.</summary>
    public Vector2 MaxXZ { get; }

    /// <summary>Creates a polygon and precomputes centroid and XZ bounds.</summary>
    /// <param name="index">Index in the owning graph.</param>
    /// <param name="vertices">At least three vertices, convex, CCW from above (array is not copied).</param>
    /// <exception cref="ArgumentException">Fewer than three vertices.</exception>
    public NavPoly(int index, Vector3[] vertices)
    {
        if (vertices == null || vertices.Length < 3)
            throw new ArgumentException("Polygon needs at least 3 vertices.", nameof(vertices));

        Index = index;
        Vertices = vertices;

        var sum = Vector3.Zero;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (var v in vertices)
        {
            sum += v;
            if (v.X < minX) minX = v.X;
            if (v.Z < minZ) minZ = v.Z;
            if (v.X > maxX) maxX = v.X;
            if (v.Z > maxZ) maxZ = v.Z;
        }

        Centroid = sum / vertices.Length;
        MinXZ = new Vector2(minX, minZ);
        MaxXZ = new Vector2(maxX, maxZ);
    }

    /// <summary>Point-in-polygon test on the XZ plane (Y ignored). Uses the
    /// standard ray-cast crossing-count algorithm — works for any convex or
    /// concave polygon, but our nav-mesh polys are always convex.</summary>
    public bool ContainsXZ(float x, float z)
    {
        if (x < MinXZ.X || x > MaxXZ.X || z < MinXZ.Y || z > MaxXZ.Y)
            return false;

        bool inside = false;
        int n = Vertices.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = Vertices[i].X, zi = Vertices[i].Z;
            float xj = Vertices[j].X, zj = Vertices[j].Z;

            bool crosses = ((zi > z) != (zj > z))
                && (x < (xj - xi) * (z - zi) / (zj - zi) + xi);
            if (crosses) inside = !inside;
        }
        return inside;
    }

    /// <summary>Sample the polygon's Y at a given XZ point via barycentric
    /// blend over the polygon's triangle fan from <see cref="Centroid"/>.
    /// Falls back to centroid Y if the point isn't actually on the polygon.</summary>
    public float SampleYAt(float x, float z)
    {
        int n = Vertices.Length;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var a = Centroid;
            var b = Vertices[i];
            var c = Vertices[next];

            // Barycentric coords on the XZ plane
            float denom = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(denom) < 1e-8f) continue;

            float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / denom;
            float v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / denom;
            float w = 1f - u - v;

            if (u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                return u * a.Y + v * b.Y + w * c.Y;
        }
        return Centroid.Y;
    }
}

/// <summary>A shared edge between two adjacent polygons (a "portal"). The
/// funnel algorithm walks portals to smooth a polygon-sequence into a
/// world-space waypoint list.</summary>
public readonly struct NavEdge
{
    /// <summary>Polygon the edge leaves.</summary>
    public readonly int FromPoly;
    /// <summary>-1 if this is a border edge (no neighbor on the other side).</summary>
    public readonly int ToPoly;
    /// <summary>Portal endpoints in world space. <c>V0</c> is on the LEFT
    /// when crossing from <c>FromPoly</c> to <c>ToPoly</c>; the funnel relies
    /// on this orientation invariant.</summary>
    public readonly Vector3 V0;
    /// <summary>Portal endpoint on the right when crossing from <c>FromPoly</c> to <c>ToPoly</c>.</summary>
    public readonly Vector3 V1;
    /// <summary>A* traversal cost (builder: distance between the two polygon centroids).</summary>
    public readonly float Cost;

    /// <summary>Creates a portal edge.</summary>
    /// <param name="fromPoly">Polygon being left.</param>
    /// <param name="toPoly">Polygon being entered, or -1 for a border edge.</param>
    /// <param name="v0">Left endpoint.</param>
    /// <param name="v1">Right endpoint.</param>
    /// <param name="cost">Traversal cost.</param>
    public NavEdge(int fromPoly, int toPoly, Vector3 v0, Vector3 v1, float cost)
    {
        FromPoly = fromPoly;
        ToPoly = toPoly;
        V0 = v0;
        V1 = v1;
        Cost = cost;
    }
}
