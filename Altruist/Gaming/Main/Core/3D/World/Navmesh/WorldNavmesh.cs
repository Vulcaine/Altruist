using System.Numerics;

namespace Altruist.Gaming.World.ThreeD;

public sealed class WorldNavMesh
{
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public int[] Indices { get; init; } = Array.Empty<int>();
}
