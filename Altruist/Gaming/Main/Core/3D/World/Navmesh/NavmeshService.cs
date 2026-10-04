/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>Per-zone nav-mesh registry + query API. Games register a
/// <see cref="NavMeshGraph"/> when a zone activates (typically by calling
/// <see cref="NavMeshBuilder.Build"/> with the zone's walkability grid +
/// terrain provider) and query paths through this service.</summary>
public interface INavMeshService
{
    void RegisterMesh(string zoneName, NavMeshGraph mesh);
    void UnregisterMesh(string zoneName);
    NavMeshGraph? GetMesh(string zoneName);
    bool HasMesh(string zoneName);

    /// <summary>Find a path from <paramref name="start"/> to <paramref name="end"/>
    /// in the named zone. Both points are first snapped to the nearest
    /// polygon within <paramref name="snapDistance"/>; if either fails to
    /// snap, the path is empty. Returns <see cref="NavPath.Empty"/> on
    /// failure rather than throwing — pathfinding sits inside hot loops
    /// (combat, AI) and exception flow there is too expensive.</summary>
    NavPath FindPath(string zoneName, Vector3 start, Vector3 end, float snapDistance = 4f);

    /// <summary>Snap a world point to the nearest position on the zone's
    /// nav-mesh. Returns the input point and false when no polygon is
    /// within <paramref name="maxDistance"/>.</summary>
    bool TrySamplePosition(string zoneName, Vector3 point, float maxDistance, out Vector3 onMesh);

    /// <summary>Straight-line walkability check on the nav-mesh: returns
    /// true iff the segment from <paramref name="start"/> to <paramref name="end"/>
    /// stays inside connected nav-mesh polygons the entire way. Useful for
    /// "do I need to actually pathfind, or can I just go straight?" — much
    /// cheaper than a full A* + funnel.</summary>
    bool IsLineWalkable(string zoneName, Vector3 start, Vector3 end);

    /// <summary>Cast a ray on the nav surface; returns true if the segment
    /// stays on the mesh, false otherwise with <paramref name="hit"/> set to
    /// the last point still on-mesh (so callers can clamp movement to the
    /// edge of a cliff/wall). For LoS-style yes/no checks where the hit
    /// point doesn't matter, <see cref="IsLineWalkable"/> is cheaper.</summary>
    bool TryRaycast(string zoneName, Vector3 start, Vector3 end, out Vector3 hit);

    /// <summary>Picks a uniformly random point on the nav-mesh. Sampling is
    /// area-weighted across polygons — bigger polys get hit proportionally
    /// more often, which is what you want for "spawn a mob somewhere on the
    /// walkable surface." Returns false when no mesh is registered for the
    /// zone.</summary>
    bool TryRandomPoint(string zoneName, Random rng, out Vector3 point);

    /// <summary>Picks a random point within <paramref name="radius"/> of
    /// <paramref name="center"/> that's on the same connected component of
    /// the mesh. Useful for "wander near my spawn anchor" / "patrol within
    /// my leash." Returns false when no walkable polygon overlaps the
    /// search circle.</summary>
    bool TryRandomPointNear(string zoneName, Vector3 center, float radius, Random rng, out Vector3 point);

    /// <summary>True when there's ANY connected sequence of polygons
    /// between the two points, without bothering to reconstruct or smooth
    /// the path. Significantly cheaper than <see cref="FindPath"/> — uses a
    /// BFS that bails on first reach.</summary>
    bool IsReachable(string zoneName, Vector3 start, Vector3 end, float snapDistance = 4f);
}

[Service(typeof(INavMeshService))]
[ConditionalOnConfig("altruist:game")]
public sealed class NavMeshService : INavMeshService
{
    private readonly ConcurrentDictionary<string, NavMeshGraph> _meshes = new();
    private readonly ILogger _logger;

    // Pathfinder is not thread-safe (per its own doc). Pool one per thread
    // so concurrent FindPath calls don't trample each other's state.
    private readonly ThreadLocal<NavMeshPathfinder> _pathfinder =
        new(() => new NavMeshPathfinder());

    public NavMeshService(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NavMeshService>();
    }

    public void RegisterMesh(string zoneName, NavMeshGraph mesh)
    {
        if (string.IsNullOrEmpty(zoneName)) throw new ArgumentException(nameof(zoneName));
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        _meshes[zoneName] = mesh;
        _logger.LogInformation(
            "[NavMesh] Registered mesh for zone '{Zone}': {Polys} polys, {Edges} edges, bounds=({MinX:F1},{MinZ:F1})→({MaxX:F1},{MaxZ:F1})",
            zoneName, mesh.PolyCount, mesh.EdgeCount,
            mesh.BoundsMin.X, mesh.BoundsMin.Z, mesh.BoundsMax.X, mesh.BoundsMax.Z);
    }

    public void UnregisterMesh(string zoneName) => _meshes.TryRemove(zoneName, out _);
    public NavMeshGraph? GetMesh(string zoneName) => _meshes.GetValueOrDefault(zoneName);
    public bool HasMesh(string zoneName) => _meshes.ContainsKey(zoneName);

    public NavPath FindPath(string zoneName, Vector3 start, Vector3 end, float snapDistance = 4f)
    {
        var mesh = GetMesh(zoneName);
        if (mesh == null) return NavPath.Empty;

        if (!mesh.TrySamplePosition(start, snapDistance, out int startPoly, out var startSnap))
            return NavPath.Empty;
        if (!mesh.TrySamplePosition(end, snapDistance, out int goalPoly, out var endSnap))
            return NavPath.Empty;

        if (!_pathfinder.Value!.TryFind(mesh, startPoly, goalPoly, out var polyPath))
            return NavPath.Empty;

        var waypoints = NavMeshFunnel.Smooth(mesh, polyPath, startSnap, endSnap);
        return new NavPath(waypoints, polyPath, isComplete: true);
    }

    public bool TrySamplePosition(string zoneName, Vector3 point, float maxDistance, out Vector3 onMesh)
    {
        var mesh = GetMesh(zoneName);
        if (mesh != null && mesh.TrySamplePosition(point, maxDistance, out _, out onMesh))
            return true;
        onMesh = point;
        return false;
    }

    public bool IsLineWalkable(string zoneName, Vector3 start, Vector3 end)
        => RaycastInternal(GetMesh(zoneName), start, end, out _);

    public bool TryRaycast(string zoneName, Vector3 start, Vector3 end, out Vector3 hit)
    {
        bool ok = RaycastInternal(GetMesh(zoneName), start, end, out hit);
        if (ok) hit = end;
        return ok;
    }

    /// <summary>Shared raycast: walks the segment in step-sized increments
    /// (half a Metin2 cell), bails the moment a probe lands off-mesh OR in
    /// a poly that isn't a direct neighbor of the previous one. On failure,
    /// <paramref name="hit"/> holds the last on-mesh probe — callers can use
    /// it to clamp movement to an obstacle's edge.</summary>
    private static bool RaycastInternal(NavMeshGraph? mesh, Vector3 start, Vector3 end, out Vector3 hit)
    {
        hit = start;
        if (mesh == null) return false;
        if (!mesh.TryLocate(start, out int currentPoly)) return false;

        float dx = end.X - start.X;
        float dz = end.Z - start.Z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist < 1e-3f) return true;

        const float StepSize = 1f;
        int steps = Math.Max(2, (int)MathF.Ceiling(dist / StepSize));
        var lastOk = start;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            var probe = new Vector3(start.X + dx * t, 0f, start.Z + dz * t);
            if (!mesh.TryLocate(probe, out int polyAt))
            {
                hit = lastOk;
                return false;
            }
            if (polyAt != currentPoly)
            {
                bool connected = false;
                foreach (var edge in mesh.GetOutgoingEdges(currentPoly))
                {
                    if (edge.ToPoly == polyAt) { connected = true; break; }
                }
                if (!connected) { hit = lastOk; return false; }
                currentPoly = polyAt;
            }
            lastOk = new Vector3(probe.X, mesh.GetPoly(polyAt).SampleYAt(probe.X, probe.Z), probe.Z);
        }
        hit = lastOk;
        return true;
    }

    public bool TryRandomPoint(string zoneName, Random rng, out Vector3 point)
    {
        point = default;
        var mesh = GetMesh(zoneName);
        if (mesh == null || mesh.PolyCount == 0 || rng == null) return false;

        // Area-weighted poly pick: cumulative XZ area, binary search by
        // a uniform sample in [0, total]. Avoids bias toward small polys
        // that a flat per-poly distribution would introduce.
        float totalArea = 0f;
        var cumulative = new float[mesh.PolyCount];
        for (int i = 0; i < mesh.PolyCount; i++)
        {
            totalArea += PolyAreaXZ(mesh.GetPoly(i));
            cumulative[i] = totalArea;
        }
        if (totalArea <= 0f) return false;

        float roll = (float)rng.NextDouble() * totalArea;
        int picked = Array.BinarySearch(cumulative, roll);
        if (picked < 0) picked = ~picked;
        if (picked >= mesh.PolyCount) picked = mesh.PolyCount - 1;

        point = SampleInsidePoly(mesh.GetPoly(picked), rng);
        return true;
    }

    public bool TryRandomPointNear(string zoneName, Vector3 center, float radius, Random rng, out Vector3 point)
    {
        point = default;
        var mesh = GetMesh(zoneName);
        if (mesh == null || mesh.PolyCount == 0 || rng == null || radius <= 0f) return false;

        // Reject-and-retry inside a circle. For the typical use case
        // (mob wander, leash radii of a few meters) this hits in 1–2 tries.
        // Cap iterations so a "circle entirely off-mesh" input fails fast.
        const int MaxAttempts = 16;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            float angle = (float)(rng.NextDouble() * 2 * Math.PI);
            float r = radius * MathF.Sqrt((float)rng.NextDouble()); // uniform on disk
            var probe = new Vector3(center.X + MathF.Cos(angle) * r, 0f, center.Z + MathF.Sin(angle) * r);
            if (mesh.TrySamplePosition(probe, maxDistance: 0.1f, out _, out var on))
            {
                point = on;
                return true;
            }
        }
        // Fallback: just try the snap on the center itself.
        if (mesh.TrySamplePosition(center, radius, out _, out var snapped))
        {
            point = snapped;
            return true;
        }
        return false;
    }

    public bool IsReachable(string zoneName, Vector3 start, Vector3 end, float snapDistance = 4f)
    {
        var mesh = GetMesh(zoneName);
        if (mesh == null) return false;
        if (!mesh.TrySamplePosition(start, snapDistance, out int startPoly, out _)) return false;
        if (!mesh.TrySamplePosition(end, snapDistance, out int goalPoly, out _)) return false;
        if (startPoly == goalPoly) return true;

        // BFS — first-reach wins. No path reconstruction, no funnel; just
        // "is there a connected sequence?". For typical zones (≤ a few
        // thousand polys) this is tens of microseconds.
        var visited = new HashSet<int> { startPoly };
        var queue = new Queue<int>();
        queue.Enqueue(startPoly);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            foreach (var edge in mesh.GetOutgoingEdges(cur))
            {
                if (edge.ToPoly < 0 || !visited.Add(edge.ToPoly)) continue;
                if (edge.ToPoly == goalPoly) return true;
                queue.Enqueue(edge.ToPoly);
            }
        }
        return false;
    }

    private static float PolyAreaXZ(NavPoly poly)
    {
        // Shoelace on the XZ plane.
        var v = poly.Vertices;
        float sum = 0f;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
            sum += (v[j].X + v[i].X) * (v[j].Z - v[i].Z);
        return MathF.Abs(sum) * 0.5f;
    }

    private static Vector3 SampleInsidePoly(NavPoly poly, Random rng)
    {
        // Triangle fan from vertex 0; pick a triangle weighted by area, then
        // pick a uniform barycentric coord inside it. Always lands inside.
        var v = poly.Vertices;
        if (v.Length == 3)
            return BarycentricSample(v[0], v[1], v[2], rng);

        int triCount = v.Length - 2;
        var triAreas = new float[triCount];
        float totalTri = 0f;
        for (int i = 0; i < triCount; i++)
        {
            triAreas[i] = TriAreaXZ(v[0], v[i + 1], v[i + 2]);
            totalTri += triAreas[i];
            triAreas[i] = totalTri;
        }

        float roll = (float)rng.NextDouble() * totalTri;
        int pick = Array.BinarySearch(triAreas, roll);
        if (pick < 0) pick = ~pick;
        if (pick >= triCount) pick = triCount - 1;
        return BarycentricSample(v[0], v[pick + 1], v[pick + 2], rng);
    }

    private static float TriAreaXZ(Vector3 a, Vector3 b, Vector3 c)
        => MathF.Abs((b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z)) * 0.5f;

    private static Vector3 BarycentricSample(Vector3 a, Vector3 b, Vector3 c, Random rng)
    {
        float u = (float)rng.NextDouble();
        float v = (float)rng.NextDouble();
        if (u + v > 1f) { u = 1f - u; v = 1f - v; }
        float w = 1f - u - v;
        return a * w + b * u + c * v;
    }
}
