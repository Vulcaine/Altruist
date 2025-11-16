using System.Text.Json;

namespace Altruist.Gaming.World.ThreeD;

public sealed class NavMeshLoader
{
    public WorldNavMesh Load(string json)
    {
        var dto = JsonSerializer.Deserialize<NavMeshData>(json)
                  ?? throw new InvalidOperationException("Invalid navmesh JSON.");

        return new WorldNavMesh
        {
            Vertices = dto.Vertices,
            Indices = dto.Indices
        };
    }
}
