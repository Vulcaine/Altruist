using System.Text.Json;

namespace Altruist.Gaming.ThreeD;

/// <summary>Parses navmesh JSON (<see cref="NavMeshSchema"/>) into a <see cref="WorldNavMesh"/>. Stateless; not a DI service.</summary>
public sealed class NavMeshLoader
{
    /// <summary>Deserializes <paramref name="json"/> with default (case-sensitive) <c>System.Text.Json</c> options.</summary>
    /// <param name="json">JSON with <c>"vertices"</c> (objects with <c>X</c>/<c>Y</c>/<c>Z</c>) and <c>"indices"</c>.</param>
    /// <returns>The loaded mesh.</returns>
    /// <exception cref="InvalidOperationException">The JSON is <c>null</c> literal.</exception>
    public WorldNavMesh Load(string json)
    {
        var dto = JsonSerializer.Deserialize<NavMeshSchema>(json)
                  ?? throw new InvalidOperationException("Invalid navmesh JSON.");

        return new WorldNavMesh
        {
            Vertices = dto.Vertices.Select(v => v.ToNumerics()).ToArray(),
            Indices = dto.Indices
        };
    }
}
