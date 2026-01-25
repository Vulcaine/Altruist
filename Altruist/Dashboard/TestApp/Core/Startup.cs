using System.Numerics;

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

public sealed class TestWorldObject : IWorldObject3D
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string ObjectArchetype { get; set; } = "test_capsule";
    public string ZoneId { get; set; } = string.Empty;
    public bool Expired { get; set; } = false;

    public string ClientId { get; set; } = string.Empty;

    public Transform3D Transform { get; set; } = Transform3D.Identity;

    public PhysxBody3DDesc? BodyDescriptor { get; set; }

    public IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }
        = Enumerable.Empty<PhysxCollider3DDesc>();

    public IEnumerable<IPhysxCollider3D> Colliders { get; set; }
        = new List<IPhysxCollider3D>();

    public IPhysxBody3D? Body { get; set; }

    public void Step(float dt, IWorldPhysics3D physics)
    {
        if (Body is not IPhysxBody3D b)
            return;

        Transform = Transform
            .WithPosition(Position3D.From(b.Position))
            .WithRotation(Rotation3D.FromQuaternion(b.Rotation));
    }
}


[AltruistModule]
public static class ServerModule
{
    [AltruistModuleLoader]
    public static async Task Initialize(
        IGameWorldOrganizer3D worldOrganizer,
        IHeightmapLoader heightmapLoader)
    {
        // ---------------------------
        // Spawn terrain (static)
        // ---------------------------

        var heightmapFile = "Resources/Heightmaps/Land_heightmap.hmap";
        var heightmapData = heightmapLoader.RAW.LoadHeightmap(heightmapFile);

        var allWorlds = worldOrganizer.GetAllWorlds().ToList();
        if (allWorlds.Count == 0)
            return;

        foreach (var world in allWorlds)
        {
            var worldSize = world.Index.Size;
            var origin = new IntVector3(0, 0, 0);

            var size = new Vector3(
                x: worldSize.X,
                y: heightmapData.HeightScale,
                z: worldSize.Z
            );

            var terrainTransform = Transform3D.From(
                origin: origin,
                rotation: Quaternion.Identity,
                size: size
            );

            await world.SpawnStaticObject(new Terrain(terrainTransform, heightmapData));
        }

        // ---------------------------
        // Spawn test humanoid capsule (dynamic)
        // ---------------------------

        var startWorld = allWorlds[0];

        var spawnPos = new Vector3(174f, 30f, 73f);
        var spawnTransform = Transform3D.Identity
            .WithPosition(Position3D.From(spawnPos))
            .WithRotation(Rotation3D.Identity)
            .WithScale(Scale3D.One);

        var prefab = new TestWorldObject
        {
            ObjectArchetype = "test_capsule",
            Transform = spawnTransform
        };

        const float radius = 0.5f;
        const float halfLength = 1.0f;

        var bodyProfile = new HumanoidCapsuleBodyProfile(radius, halfLength, 75f);

        prefab.BodyDescriptor = bodyProfile.CreateBody(prefab.Transform);
        prefab.ColliderDescriptors = bodyProfile.CreateColliders(prefab.Transform);

        var clientId = "test-client";
        IPhysxBody3D? body = await startWorld.SpawnDynamicObject(prefab, withId: clientId);
    }
}
