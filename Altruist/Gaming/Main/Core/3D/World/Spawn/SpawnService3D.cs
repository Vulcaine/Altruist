/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.World.ThreeD;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Spawns world objects into a 3D game world using engine-agnostic descriptors
    /// stored on the world object (Transform + BodyDescriptor).
    /// </summary>
    public interface ISpawnService3D
    {
        /// <summary>
        /// Materializes the given world object into the specified game world's
        /// physics world, based on its BodyDescriptor and Transform.
        /// </summary>
        IWorldObject3D Spawn(IGameWorldManager3D world, IWorldObject3D obj);
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
        }

        public IWorldObject3D Spawn(IGameWorldManager3D world, IWorldObject3D obj)
        {
            if (world is null)
                throw new ArgumentNullException(nameof(world));
            if (obj is null)
                throw new ArgumentNullException(nameof(obj));

            // Resolve the 3D physics world + engine.
            if (world.PhysxWorld is not IPhysxWorld3D physxWorld3D)
                throw new InvalidOperationException("Game world must expose an IPhysxWorld3D physics world.");

            var engine3D = world.PhysxWorld.Engine;

            var bodyDesc = obj.BodyDescriptor ?? PhysxBody3D.Create(
                PhysxBodyType.Dynamic,
                mass: 1f,
                transform: obj.Transform);

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
    }
}
