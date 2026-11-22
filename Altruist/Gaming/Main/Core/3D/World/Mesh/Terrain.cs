using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Static heightfield terrain world object: a static body on the <c>Terrain</c> physics layer with one heightmap collider.
/// Spawn it with <see cref="IGameWorldManager3D.SpawnStaticObject"/>; its partition bounds cover the whole heightfield grid.
/// </summary>
/// <remarks>
/// For gameplay height queries without physics use an <see cref="ITerrainProvider"/>; this type only feeds the physics
/// engine and spatial partitions.
/// </remarks>
/// <example>
/// <code>
/// await world.SpawnStaticObject(new Terrain(terrainTransform, heightfieldData));
/// </code>
/// </example>
public sealed class Terrain : WorldObject3D
{
    private readonly HeightfieldData _heightmapData;

    /// <summary>Creates the terrain and its body/collider descriptors.</summary>
    /// <param name="transform">World transform of the heightfield (its position is used as the grid's minimum X/Z corner for partition bounds).</param>
    /// <param name="heightmapData">Height samples and cell sizes.</param>
    /// <param name="zoneId">Room/zone id.</param>
    /// <param name="archetype">Initial archetype (replaced on spawn by attribute resolution, i.e. <c>""</c>).</param>
    public Terrain(Transform3D transform, HeightfieldData heightmapData, string zoneId = "", string? archetype = null)
        : base(transform, zoneId, archetype)
    {
        _heightmapData = heightmapData;
        BodyDescriptor = PhysxBody3D.Create(
            PhysxBodyType.Static,
            mass: 0f,
            transform: transform,
            physxTag: new PhysxTag((uint)PhysxLayer.Terrain));

        ColliderDescriptors =
        [
            PhysxCollider3D.CreateHeightmap(
                _heightmapData,
                transform,
                isTrigger: false)
        ];
    }
}

