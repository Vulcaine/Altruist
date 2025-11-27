/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Spawns world objects into a 3D game world using engine-agnostic descriptors
    /// stored on the world object (Transform + BodyDescriptor).
    /// Also integrates with the prefab factory so callers can spawn by prefab type.
    /// </summary>
    public interface ISpawnService3D
    {
        /// <summary>
        /// Materializes the given world object into the specified game world's
        /// physics world, based on its BodyDescriptor and Transform.
        /// </summary>
        WorldObjectPrefab3D Spawn(IGameWorldManager3D world, WorldObjectPrefab3D obj);

        /// <summary>
        /// Same as Spawn(world, obj) but assigns a zone/room id before spawning.
        /// </summary>
        WorldObjectPrefab3D Spawn(IGameWorldManager3D world, WorldObjectPrefab3D obj, string zoneId);
    }

    [Service(typeof(ISpawnService3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class SpawnService3D : ISpawnService3D
    {
        private readonly IPhysxBodyApiProvider3D _bodyApi;
        private readonly IPhysxColliderApiProvider3D _colliderApi;

        public SpawnService3D(
            IPhysxBodyApiProvider3D bodyApi,
            IPhysxColliderApiProvider3D colliderApi)
        {
            _bodyApi = bodyApi ?? throw new ArgumentNullException(nameof(bodyApi));
            _colliderApi = colliderApi ?? throw new ArgumentNullException(nameof(colliderApi));
            ;
        }

        /// <inheritdoc />
        public WorldObjectPrefab3D Spawn(IGameWorldManager3D world, WorldObjectPrefab3D obj)
        {
            if (world is null)
                throw new ArgumentNullException(nameof(world));
            if (obj is null)
                throw new ArgumentNullException(nameof(obj));

            // Ensure archetype is resolved even if caller passed in an instance.
            obj.Archetype = WorldObjectArchetypeHelper.ResolveArchetype(obj.GetType());

            if (world.PhysxWorld is not IPhysxWorld3D physxWorld3D)
                throw new InvalidOperationException("Game world must expose an IPhysxWorld3D physics world.");

            var engine3D = physxWorld3D.Engine;

            // Respect any BodyDescriptor already set on the prefab; otherwise
            // create a default dynamic body.
            var bodyDesc = obj.BodyDescriptor ?? PhysxBody3D.Create(
                PhysxBodyType.Dynamic,
                mass: 1f,
                transform: obj.Transform);

            // TODO: support colliderdescriptors
            var colliderDesc = PhysxCollider3D.Create(
                PhysxColliderShape3D.Box3D,
                bodyDesc.Transform,
                isTrigger: false);

            var body = _bodyApi.CreateBody(engine3D, bodyDesc);
            var collider = _colliderApi.CreateCollider(colliderDesc);

            _bodyApi.AddCollider(engine3D, body, collider);
            physxWorld3D.AddBody(body);

            obj.BodyDescriptor = bodyDesc;

            return obj;
        }

        /// <inheritdoc />
        public WorldObjectPrefab3D Spawn(IGameWorldManager3D world, WorldObjectPrefab3D obj, string zoneId)
        {
            if (obj is null)
                throw new ArgumentNullException(nameof(obj));

            SetZone(obj, zoneId ?? string.Empty);
            return Spawn(world, obj);
        }

        private static void SetZone(WorldObjectPrefab3D obj, string zoneId)
        {
            obj.ZoneId = zoneId;
        }
    }
}
