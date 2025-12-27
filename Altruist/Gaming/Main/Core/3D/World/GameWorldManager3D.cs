/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

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

        /// <summary>
        /// Find all partitions intersecting a sphere around a position.
        /// Useful for "what partitions does this player see?" style queries.
        /// </summary>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius);

        /// <summary>
        /// Find a single partition that contains a specific position (if any).
        /// </summary>
        WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z);

        /// <summary>
        /// Find all partitions whose AABB intersects the provided bounds.
        /// </summary>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForBounds(
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ);

        /// <summary>
        /// Find all partitions that intersect the bounds of the given object.
        /// </summary>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForObject(IWorldObject3D obj);
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

        /// <summary>
        /// Recalculate which partitions contain the given object, based on its bounds.
        /// </summary>
        public async Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj)
        {
            if (obj is null)
                return Enumerable.Empty<WorldPartitionManager3D>();

            // Remove from all old partitions / caches
            DestroyObject(obj);

            var partitions = FindPartitionsForObject(obj);
            AddObjectToPartitions(obj, partitions);

            // NOTE: previously this method did not re-add to _flatInstanceCache either;
            // preserving that behavior for now.
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

            obj.ObjectArchetype = obj is AnonymousWorldObject3D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper.ResolveArchetype(obj.GetType());

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
                colliderDescs =
                [
                    PhysxCollider3D.Create(
                        PhysxColliderShape3D.Box3D,
                        bodyDesc.Transform,
                        isTrigger: false)
                ];
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

            // Bounds-based partition registration: big objects (e.g. landscape)
            // get added to every partition their collider bounds touch.
            var partitions = FindPartitionsForObject(obj);
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

            // Let all partitions try to destroy the object.
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

        /// <summary>
        /// Sphere-based query: find partitions intersecting a radius around a point.
        /// Kept for visibility queries (player perspective).
        /// </summary>
        public IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;
            float minZ = z - radius;
            float maxZ = z + radius;

            return FindPartitionsForBounds(minX, minY, minZ, maxX, maxY, maxZ);
        }

        /// <summary>
        /// AABB-based query: find partitions whose bounds intersect the given bounds.
        /// </summary>
        public IEnumerable<WorldPartitionManager3D> FindPartitionsForBounds(
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ)
        {
            // Simple AABB intersection test between query bounds and partition bounds.
            return _partitions.Where(partition =>
                maxX >= partition.Position.X &&
                minX <= partition.Position.X + partition.Size.X &&
                maxY >= partition.Position.Y &&
                minY <= partition.Position.Y + partition.Size.Y &&
                maxZ >= partition.Position.Z &&
                minZ <= partition.Position.Z + partition.Size.Z
            );
        }

        /// <summary>
        /// Compute an object's axis-aligned bounds.
        /// Preferred source = non-trigger collider descriptor.
        /// If no collider descriptors exist (or something looks degenerate),
        /// we fall back to the object's Transform (old behavior).
        /// </summary>
        private static (float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
            GetObjectBounds(IWorldObject3D obj)
        {
            PhysxCollider3DDesc? chosen = null;
            PhysxCollider3DDesc? firstAny = null;

            var colliders = obj.ColliderDescriptors;
            if (colliders != null)
            {
                foreach (var c in colliders)
                {
                    if (!firstAny.HasValue)
                        firstAny = c;

                    if (!c.IsTrigger)
                    {
                        chosen = c;
                        break;
                    }
                }

                if (!chosen.HasValue && firstAny.HasValue)
                    chosen = firstAny;
            }

            Transform3D transformToUse;

            if (chosen.HasValue)
            {
                transformToUse = chosen.Value.Transform;
            }
            else
            {
                transformToUse = obj.Transform;
            }

            var pos = transformToUse.Position;
            var size = transformToUse.Size;
            bool colliderDegenerate =
                size.X == 0f && size.Y == 0f && size.Z == 0f ||
                float.IsNaN(size.X) || float.IsNaN(size.Y) || float.IsNaN(size.Z) ||
                float.IsInfinity(size.X) || float.IsInfinity(size.Y) || float.IsInfinity(size.Z);

            if (colliderDegenerate)
            {
                var objSize = obj.Transform.Size;
                if (!(objSize.X == 0f && objSize.Y == 0f && objSize.Z == 0f))
                {
                    size = objSize;
                    pos = obj.Transform.Position;
                }
            }

            var halfX = size.X * 0.5f;
            var halfY = size.Y * 0.5f;
            var halfZ = size.Z * 0.5f;

            var minX = pos.X - halfX;
            var maxX = pos.X + halfX;
            var minY = pos.Y - halfY;
            var maxY = pos.Y + halfY;
            var minZ = pos.Z - halfZ;
            var maxZ = pos.Z + halfZ;

            return (minX, minY, minZ, maxX, maxY, maxZ);
        }

        /// <summary>
        /// Find all partitions intersecting the bounds of the given object.
        /// </summary>
        public IEnumerable<WorldPartitionManager3D> FindPartitionsForObject(IWorldObject3D obj)
        {
            if (obj is null)
                return Enumerable.Empty<WorldPartitionManager3D>();

            var (minX, minY, minZ, maxX, maxY, maxZ) = GetObjectBounds(obj);
            return FindPartitionsForBounds(minX, minY, minZ, maxX, maxY, maxZ);
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
        /// Kept for callers that still want a radius-based query.
        /// </summary>
        private static float ComputePartitionRadius(IWorldObject3D obj)
        {
            var sz = obj.Transform.Size;
            var r = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z)) * 0.5f;

            if (r <= 0f || float.IsNaN(r) || float.IsInfinity(r))
                r = 0.5f; // minimal sensible radius
            return r;
        }

        public IWorldObject3D? FindObject(string id)
            => _flatInstanceCache.TryGetValue(id, out var obj) ? obj : null;

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
