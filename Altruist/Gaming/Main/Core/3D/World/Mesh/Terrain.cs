using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

public sealed class Terrain : WorldObject3D
{
    private readonly HeightfieldData _heightmapData;

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

