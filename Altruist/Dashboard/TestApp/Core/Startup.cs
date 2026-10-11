using System.Numerics;

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

/// <summary>Sample dynamic world object whose transform follows its physics body (dashboard test app only).</summary>
public sealed class TestWorldObject : IWorldObject3D
{
    /// <summary>Unique instance id.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Archetype name.</summary>
    public string ObjectArchetype { get; set; } = "test_capsule";
    /// <summary>Zone id.</summary>
    public string ZoneId { get; set; } = string.Empty;
    /// <summary>Whether the object should be removed.</summary>
    public bool Expired { get; set; } = false;

    /// <summary>Owning client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Numeric id assigned by the world.</summary>
    public uint VirtualId { get; set; }

    /// <summary>Collision layer mask (all layers by default).</summary>
    public uint CollisionLayer { get; set; } = 0xFFFFFFFFu;

    /// <summary>World transform.</summary>
    public Transform3D Transform { get; set; } = Transform3D.Identity;

    /// <summary>Physics body description used at spawn.</summary>
    public PhysxBody3DDesc? BodyDescriptor { get; set; }

    /// <summary>Collider descriptions used at spawn.</summary>
    public IEnumerable<PhysxCollider3DDesc> ColliderDescriptors { get; set; }
        = Enumerable.Empty<PhysxCollider3DDesc>();

    /// <summary>Created colliders.</summary>
    public IEnumerable<IPhysxCollider3D> Colliders { get; set; }
        = new List<IPhysxCollider3D>();

    /// <summary>Created physics body.</summary>
    public IPhysxBody3D? Body { get; set; }

    /// <summary>Copies the body's position and rotation into <see cref="Transform"/>; no-op without a body.</summary>
    /// <param name="dt">Step length in seconds (unused).</param>
    /// <param name="world">Owning world (unused).</param>
    public void Step(float dt, IGameWorldManager3D world)
    {
        if (Body is not IPhysxBody3D b)
            return;

        Transform = Transform
            .WithPosition(Position3D.From(b.Position))
            .WithRotation(Rotation3D.FromQuaternion(b.Rotation));
    }
}


/// <summary>Sample module that populates every world with a heightmap terrain and one test capsule.</summary>
[AltruistModule]
public static class ServerModule
{
    /// <summary>
    /// Loads <c>Resources/Heightmaps/Land_heightmap.hmap</c>, spawns it as static terrain in every world, then spawns a
    /// dynamic capsule with client id <c>test-client</c> in the first world. Does nothing when there are no worlds.
    /// </summary>
    /// <param name="worldOrganizer">Supplies the worlds.</param>
    /// <param name="heightmapLoader">Loads the RAW heightmap.</param>
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
