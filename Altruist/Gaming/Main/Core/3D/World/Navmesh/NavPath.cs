/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>The result of a nav-mesh query: a list of waypoints from start
/// to goal, with the polygon sequence preserved for callers that need it
/// (e.g. agents that re-validate they're still on the expected polygon).</summary>
public sealed class NavPath
{
    /// <summary>World-space waypoints, starting at the snapped start and ending at the snapped goal; empty when no path.</summary>
    public IReadOnlyList<Vector3> Waypoints { get; }
    /// <summary>Polygon indices visited, start to goal inclusive.</summary>
    public IReadOnlyList<int> Polygons { get; }
    /// <summary>True when the path actually reached the requested goal. False
    /// for partial paths where the goal was unreachable but a closest-poly
    /// fallback was returned (currently never produced — kept for future
    /// extension where partial paths matter, e.g. monster chase AI).</summary>
    public bool IsComplete { get; }
    /// <summary>Total XZ length of the waypoint polyline in world units (Y ignored).</summary>
    public float Length { get; }

    /// <summary>Creates a path and computes its <see cref="Length"/>.</summary>
    /// <param name="waypoints">World-space waypoints.</param>
    /// <param name="polygons">Polygon sequence.</param>
    /// <param name="isComplete">Whether the goal was reached.</param>
    public NavPath(IReadOnlyList<Vector3> waypoints, IReadOnlyList<int> polygons, bool isComplete)
    {
        Waypoints = waypoints;
        Polygons = polygons;
        IsComplete = isComplete;

        float len = 0f;
        for (int i = 1; i < waypoints.Count; i++)
        {
            float dx = waypoints[i].X - waypoints[i - 1].X;
            float dz = waypoints[i].Z - waypoints[i - 1].Z;
            len += MathF.Sqrt(dx * dx + dz * dz);
        }
        Length = len;
    }

    /// <summary>Shared empty, incomplete path returned on failure (check <c>Waypoints.Count</c> or <see cref="IsComplete"/>).</summary>
    public static NavPath Empty { get; } =
        new NavPath(Array.Empty<Vector3>(), Array.Empty<int>(), isComplete: false);
}
