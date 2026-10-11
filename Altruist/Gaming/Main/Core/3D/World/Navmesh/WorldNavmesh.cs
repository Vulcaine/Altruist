using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Raw triangle navmesh (vertices + triangle indices) as loaded from JSON by <see cref="NavMeshLoader"/>. It is plain data:
/// the framework's pathfinding uses <see cref="NavMeshGraph"/> (built with <see cref="NavMeshBuilder"/> and registered in
/// <see cref="INavMeshService"/>), and nothing converts this type into a graph.
/// </summary>
public sealed class WorldNavMesh
{
    /// <summary>Vertex positions in world space.</summary>
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    /// <summary>Triangle list: three indices into <see cref="Vertices"/> per triangle.</summary>
    public int[] Indices { get; init; } = Array.Empty<int>();
}
