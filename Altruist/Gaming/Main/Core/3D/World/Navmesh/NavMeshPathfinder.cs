/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>A* over the polygon graph. Returns the sequence of polygon
/// indices from start to goal (inclusive on both ends). The cost of moving
/// between adjacent polys is the pre-baked centroid distance stored on the
/// edge; the heuristic is straight-line distance to the goal centroid.
///
/// Stateful per-call: not thread-safe. Allocate one per worker, or call from
/// a single thread.</summary>
public sealed class NavMeshPathfinder
{
    // Open set as a binary min-heap on f-score.
    private readonly PriorityQueue<int, float> _open = new();
    private readonly Dictionary<int, int> _cameFrom = new();
    private readonly Dictionary<int, float> _gScore = new();
    private readonly HashSet<int> _closed = new();

    /// <summary>Find a polygon-level path. Returns false if no path exists
    /// (disconnected components, or either index out of range).</summary>
    public bool TryFind(NavMeshGraph graph, int startPoly, int goalPoly, out int[] polyPath)
    {
        polyPath = Array.Empty<int>();
        if (graph == null || startPoly < 0 || startPoly >= graph.PolyCount
            || goalPoly < 0 || goalPoly >= graph.PolyCount)
            return false;

        if (startPoly == goalPoly)
        {
            polyPath = new[] { startPoly };
            return true;
        }

        _open.Clear();
        _cameFrom.Clear();
        _gScore.Clear();
        _closed.Clear();

        var goalCentroid = graph.GetPoly(goalPoly).Centroid;

        _gScore[startPoly] = 0f;
        _open.Enqueue(startPoly, Heuristic(graph.GetPoly(startPoly).Centroid, goalCentroid));

        while (_open.TryDequeue(out int current, out _))
        {
            if (current == goalPoly)
            {
                polyPath = ReconstructPath(current);
                return true;
            }

            if (!_closed.Add(current))
                continue;

            float gCurrent = _gScore[current];

            foreach (var edge in graph.GetOutgoingEdges(current))
            {
                int neighbor = edge.ToPoly;
                if (neighbor < 0 || _closed.Contains(neighbor)) continue;

                float tentativeG = gCurrent + edge.Cost;
                if (_gScore.TryGetValue(neighbor, out float existing) && tentativeG >= existing)
                    continue;

                _cameFrom[neighbor] = current;
                _gScore[neighbor] = tentativeG;
                float f = tentativeG + Heuristic(graph.GetPoly(neighbor).Centroid, goalCentroid);
                _open.Enqueue(neighbor, f);
            }
        }

        return false;
    }

    private int[] ReconstructPath(int last)
    {
        // Walk cameFrom backward to start.
        var stack = new Stack<int>();
        int cur = last;
        stack.Push(cur);
        while (_cameFrom.TryGetValue(cur, out int prev))
        {
            stack.Push(prev);
            cur = prev;
        }
        return stack.ToArray();
    }

    private static float Heuristic(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
