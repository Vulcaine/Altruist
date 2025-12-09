/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldManager3D : IGameWorldManager
    {
        IWorldIndex3D Index { get; }
        IPhysxWorld3D PhysxWorld { get; }

        Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj);

        IWorldObject3D? FindObject(string id);
        IEnumerable<T> FindAllObjects<T>() where T : IWorldObject3D;
        IEnumerable<IWorldObject3D> GetAllObjects();
        Task<IPhysxBody3D?> SpawnDynamicObject(IWorldObject3D obj, string? withId = null);
        Task<IPhysxBody3D?> SpawnStaticObject(IWorldObject3D obj, string? withId = null);
        IWorldObject3D? DestroyObject(string instanceId);
        IWorldObject3D? DestroyObject(IWorldObject3D obj);

        IEnumerable<IWorldObject3D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId);

        IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius);
        WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z);
    }

    public sealed class GameWorldManager3D : IGameWorldManager3D
    {
        private readonly IWorldIndex3D _index;
        private readonly IWorldPartitioner3D _worldPartitioner;
        private readonly Dictionary<PartitionIndex3D, WorldPartitionManager3D> _partitionMap = new();

        private readonly List<WorldPartitionManager3D> _partitions;
        private readonly IPhysxWorld3D _physx3D;

        private readonly IPhysxBodyApiProvider3D _bodyApi;
        private readonly IPhysxColliderApiProvider3D _colliderApi;

        private readonly Dictionary<string, IWorldObject3D> _flatInstanceCache = new();

        public GameWorldManager3D(
            IWorldIndex3D world,
            IPhysxWorld3D physx3D,
            IWorldPartitioner3D worldPartitioner,
            IPhysxBodyApiProvider3D bodyApi,
            IPhysxColliderApiProvider3D colliderApi
        )
        {
            _index = world;
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _worldPartitioner = worldPartitioner;

            _physx3D = physx3D;
            _partitions = new List<WorldPartitionManager3D>();
            Initialize();
        }

        public IPhysxWorld3D PhysxWorld => _physx3D;
        public IWorldIndex3D Index => _index;

        public void Initialize()
        {
            var partitions = _worldPartitioner.CalculatePartitions(_index);
            foreach (var partition in partitions)
            {
                _partitions.Add(partition);
                _partitionMap[new PartitionIndex3D(partition.Index.X, partition.Index.Y, partition.Index.Z)] = partition;
            }

        }


        public async Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj)
        {
            if (obj is null)
                return Enumerable.Empty<WorldPartitionManager3D>();

            DestroyObject(obj);

            var radius = ComputePartitionRadius(obj);
            var partitions = FindPartitionsForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                obj.Transform.Position.Z,
                radius);

            AddObjectToPartitions(obj, partitions);
            return await Task.FromResult(partitions.ToList());
        }

        public async Task<IPhysxBody3D?> SpawnDynamicObject(IWorldObject3D obj, string? withId = null)
        {
            return await SpawnObjectInternal(
                obj,
                bodyType: PhysxBodyType.Dynamic,
                isStatic: false,
                withId: withId);
        }

        public async Task<IPhysxBody3D?> SpawnStaticObject(IWorldObject3D obj, string? withId = null)
        {
            return await SpawnObjectInternal(
                obj,
                bodyType: PhysxBodyType.Static,
                isStatic: true,
                withId: withId);
        }

        /// <summary>
        /// Core spawn logic shared by dynamic & static world objects.
        /// </summary>
        private async Task<IPhysxBody3D?> SpawnObjectInternal(
     IWorldObject3D obj,
     PhysxBodyType bodyType,
     bool isStatic,
     string? withId = null)
        {
            if (obj is null)
                return null;

            obj.Archetype = obj is AnonymousWorldObject3D ? obj.Archetype : WorldObjectArchetypeHelper.ResolveArchetype(obj.GetType());

            var engine3D = PhysxWorld.Engine;
            var mass = isStatic ? 0f : 1f;

            var bodyDesc = obj.BodyDescriptor ?? PhysxBody3D.Create(
                bodyType,
                mass: mass,
                transform: obj.Transform);

            // Use existing collider descriptors if present, otherwise create a default box.
            var colliderDescs = obj.ColliderDescriptors;
            if (colliderDescs == null || !colliderDescs.Any())
            {
                colliderDescs = new[]
                {
            PhysxCollider3D.Create(
                PhysxColliderShape3D.Box3D,
                bodyDesc.Transform,
                isTrigger: false)
        };
            }

            var body = _bodyApi.CreateBody(engine3D, bodyDesc);

            var createdColliders = new List<IPhysxCollider3D>();
            foreach (var cd in colliderDescs)
            {
                var collider = _colliderApi.CreateCollider(cd);
                _bodyApi.AddCollider(engine3D, body, collider);
                createdColliders.Add(collider);
            }

            obj.BodyDescriptor = bodyDesc;
            obj.Body = body;
            obj.ColliderDescriptors = colliderDescs;
            obj.Colliders = createdColliders;

            // partition registration stays as-is...
            IEnumerable<WorldPartitionManager3D> partitions;

            if (isStatic)
            {
                var p = FindPartitionForPosition(
                    obj.Transform.Position.X,
                    obj.Transform.Position.Y,
                    obj.Transform.Position.Z);

                partitions = p is null
                    ? Enumerable.Empty<WorldPartitionManager3D>()
                    : new[] { p };
            }
            else
            {
                var radius = ComputePartitionRadius(obj);
                partitions = FindPartitionsForPosition(
                    obj.Transform.Position.X,
                    obj.Transform.Position.Y,
                    obj.Transform.Position.Z,
                    radius);
            }

            foreach (var p in partitions)
                p.AddObject(obj);

            if (withId != null)
                _flatInstanceCache[withId] = obj;
            else
                _flatInstanceCache[obj.InstanceId] = obj;

            PhysxWorld.AddBody(body);
            await Task.CompletedTask;
            return body;
        }

        public IWorldObject3D? DestroyObject(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            // First let all partitions try to destroy the object.
            // Keep the first non-null result if they return it.
            var removedFromPartitions = _partitions
                .Select(p => p.DestroyObject(instanceId))
                .FirstOrDefault(o => o != null);

            if (_flatInstanceCache.TryGetValue(instanceId, out var cachedByKey))
            {
                _flatInstanceCache.Remove(instanceId);
                return removedFromPartitions ?? cachedByKey;
            }

            var kvp = _flatInstanceCache
                .FirstOrDefault(x => x.Value.InstanceId == instanceId);

            if (!string.IsNullOrEmpty(kvp.Key))
            {
                _flatInstanceCache.Remove(kvp.Key);
                return removedFromPartitions ?? kvp.Value;
            }

            return removedFromPartitions;
        }

        public IWorldObject3D? DestroyObject(IWorldObject3D obj)
            => DestroyObject(obj.InstanceId);

        public IEnumerable<IWorldObject3D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            var result = new List<IWorldObject3D>();
            var partitions = FindPartitionsForPosition(x, y, z, radius);
            foreach (var partition in partitions)
                result.AddRange(partition.GetObjectsByTypeInRadius(archetype, x, y, z, radius, roomId));

            return result.Distinct();
        }

        public WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);
            int indexZ = (int)Math.Round(z / (double)_worldPartitioner.PartitionDepth);

            return _partitionMap.TryGetValue(new PartitionIndex3D(indexX, indexY, indexZ), out var p) ? p : null;
        }

        public IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;
            float minZ = z - radius;
            float maxZ = z + radius;

            return _partitions.Where(partition =>
                maxX >= partition.Position.X &&
                minX <= partition.Position.X + partition.Size.X &&
                maxY >= partition.Position.Y &&
                minY <= partition.Position.Y + partition.Size.Y &&
                maxZ >= partition.Position.Z &&
                minZ <= partition.Position.Z + partition.Size.Z
            );
        }

        private IEnumerable<WorldPartitionManager3D> AddObjectToPartitions(
            IWorldObject3D obj,
            IEnumerable<WorldPartitionManager3D> partitions
        )
        {
            foreach (var partition in partitions)
                partition.AddObject(obj);

            return partitions;
        }

        /// <summary>
        /// Compute a partition search radius from the object's transform size.
        /// Uses half of the largest dimension; minimal floor if degenerate.
        /// </summary>
        private static float ComputePartitionRadius(IWorldObject3D obj)
        {
            var sz = obj.Transform.Size;
            var r = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z)) * 0.5f;

            if (r <= 0f || float.IsNaN(r) || float.IsInfinity(r))
                r = 0.5f; // minimal sensible radius
            return r;
        }

        public IWorldObject3D? FindObject(string id) => _flatInstanceCache.TryGetValue(id, out var obj) ? obj : null;
        public IEnumerable<T> FindAllObjects<T>() where T : IWorldObject3D
        {
            return _flatInstanceCache.Values
                .OfType<T>();
        }

        public IEnumerable<IWorldObject3D> GetAllObjects()
        {
            return _flatInstanceCache.Values;
        }
    }
}
