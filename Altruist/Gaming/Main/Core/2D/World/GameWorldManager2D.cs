/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    public interface IGameWorldManager2D : IGameWorldManager
    {
        IWorldIndex2D Index { get; }
        IPhysxWorld PhysxWorld { get; }
        void Initialize();
        Task SaveAsync();

        IWorldObject2D? FindObject(string id);
        IEnumerable<T> FindAllObjects<T>() where T : IWorldObject2D;
        IEnumerable<IWorldObject2D> GetAllObjects();

        Task<IEnumerable<IWorldPartitionManager>> UpdateObjectPosition(IWorldObject2D obj);

        Task<IPhysxBody2D?> SpawnDynamicObject(IWorldObject2D obj, string? withId = null);
        IWorldPartitionManager? SpawnStaticObject(IWorldObject2D obj, string? withId = null);

        /// <summary>Legacy alias for <see cref="SpawnDynamicObject"/>.</summary>
        Task AddDynamicObject(IWorldObject2D obj);
        /// <summary>Legacy alias for <see cref="SpawnStaticObject"/>.</summary>
        IWorldPartitionManager? AddStaticObject(IWorldObject2D obj);

        IWorldObject2D? DestroyObject(string instanceId);
        IWorldObject2D? DestroyObject(IWorldObject2D obj);

        IEnumerable<IWorldObject2D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y,
            float radius,
            string roomId);

        IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius);
        IWorldPartitionManager? FindPartitionForPosition(int x, int y);

        /// <summary>Find all partitions whose AABB intersects the provided bounds.</summary>
        IEnumerable<IWorldPartitionManager> FindPartitionsForBounds(
            float minX, float minY,
            float maxX, float maxY);

        /// <summary>Find all partitions that intersect the bounds of the given object.</summary>
        IEnumerable<IWorldPartitionManager> FindPartitionsForObject(IWorldObject2D obj);
    }

    public sealed class GameWorldManager2D : IGameWorldManager2D
    {
        private readonly IWorldIndex2D _index;
        private readonly IWorldPartitioner2D _worldPartitioner;
        private readonly ICacheProvider? _cache;
        private readonly Dictionary<PartitionIndex2D, IWorldPartitionManager> _partitionMap = new();

        private readonly List<WorldPartition2D> _partitions;
        private readonly IPhysxWorld2D _physx2D;

        private readonly IPhysxBodyApiProvider2D? _bodyApi;
        private readonly IPhysxColliderApiProvider2D? _colliderApi;

        private readonly Dictionary<string, IWorldObject2D> _flatInstanceCache = new();

        public GameWorldManager2D(
            IWorldIndex2D world,
            IPhysxWorld2D physx2D,
            IWorldPartitioner2D worldPartitioner,
            ICacheProvider? cacheProvider = null,
            IPhysxBodyApiProvider2D? bodyApi = null,
            IPhysxColliderApiProvider2D? colliderApi = null
        )
        {
            _index = world ?? throw new ArgumentNullException(nameof(world));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));
            _cache = cacheProvider;
            _physx2D = physx2D ?? throw new ArgumentNullException(nameof(physx2D));
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _partitions = new List<WorldPartition2D>();
        }

        public IPhysxWorld PhysxWorld => _physx2D;
        public IWorldIndex2D Index => _index;

        public void Initialize()
        {
            var partitions = _worldPartitioner.CalculatePartitions(_index);
            foreach (var partition in partitions)
            {
                _partitions.Add(partition);
                _partitionMap[new PartitionIndex2D(partition.Index.X, partition.Index.Y)] = partition;
            }

            _ = SaveAsync();
        }

        public async Task SaveAsync()
        {
            if (_cache is null)
                return;
            var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.StorageId, p));
            await Task.WhenAll(saveTasks);
        }

        // ── Lookup ──────────────────────────────────────────────────────────────

        public IWorldObject2D? FindObject(string id)
            => _flatInstanceCache.TryGetValue(id, out var obj) ? obj : null;

        public IEnumerable<T> FindAllObjects<T>() where T : IWorldObject2D
            => _flatInstanceCache.Values.OfType<T>();

        public IEnumerable<IWorldObject2D> GetAllObjects()
            => _flatInstanceCache.Values;

        // ── Spawn ───────────────────────────────────────────────────────────────

        public async Task<IPhysxBody2D?> SpawnDynamicObject(IWorldObject2D obj, string? withId = null)
        {
            if (obj is null)
                return null;

            obj.ObjectArchetype = obj is AnonymousWorldObject2D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper2D.ResolveArchetype(obj.GetType());

            IPhysxBody2D? body = null;

            if (_bodyApi != null)
            {
                body = _bodyApi.CreateBody(PhysxBodyType.Dynamic, mass: 1f, obj.Transform);

                if (_colliderApi != null)
                {
                    var collider = _colliderApi.CreateCollider(
                        new PhysxCollider2DParams(PhysxColliderShape2D.Box2D, obj.Transform, isTrigger: false));
                    _bodyApi.AddCollider(body, collider);
                }

                obj.Body = body;
                _physx2D.AddBody(body);
            }

            var partitions = FindPartitionsForObject(obj);
            foreach (var p in partitions)
                if (p is WorldPartition2D p2d) p2d.AddObject(obj);

            _flatInstanceCache[withId ?? obj.InstanceId] = obj;

            await Task.CompletedTask;
            return body;
        }

        public IWorldPartitionManager? SpawnStaticObject(IWorldObject2D obj, string? withId = null)
        {
            if (obj is null)
                return null;

            obj.ObjectArchetype = obj is AnonymousWorldObject2D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper2D.ResolveArchetype(obj.GetType());

            IPhysxBody2D? body = null;

            if (_bodyApi != null)
            {
                body = _bodyApi.CreateBody(PhysxBodyType.Static, mass: 0f, obj.Transform);

                if (_colliderApi != null)
                {
                    var collider = _colliderApi.CreateCollider(
                        new PhysxCollider2DParams(PhysxColliderShape2D.Box2D, obj.Transform, isTrigger: false));
                    _bodyApi.AddCollider(body, collider);
                }

                obj.Body = body;
                _physx2D.AddBody(body);
            }

            var partition = FindPartitionForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y);

            if (partition is WorldPartition2D p2d)
                p2d.AddObject(obj);

            _flatInstanceCache[withId ?? obj.InstanceId] = obj;

            return partition;
        }

        // ── Legacy aliases ──────────────────────────────────────────────────────

        public async Task AddDynamicObject(IWorldObject2D obj)
            => await SpawnDynamicObject(obj);

        public IWorldPartitionManager? AddStaticObject(IWorldObject2D obj)
            => SpawnStaticObject(obj);

        // ── Destroy ─────────────────────────────────────────────────────────────

        public IWorldObject2D? DestroyObject(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            var removedFromPartitions = _partitions
                .Select(p => p.DestroyObject(instanceId))
                .FirstOrDefault(o => o != null);

            IWorldObject2D? removedFromCache = null;

            if (_flatInstanceCache.TryGetValue(instanceId, out var cachedByKey))
            {
                removedFromCache = cachedByKey;
                _flatInstanceCache.Remove(instanceId);
            }
            else
            {
                var kvp = _flatInstanceCache.FirstOrDefault(x => x.Value?.InstanceId == instanceId);
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    removedFromCache = kvp.Value;
                    _flatInstanceCache.Remove(kvp.Key);
                }
            }

            var obj = removedFromPartitions ?? removedFromCache;

            if (obj?.Body != null)
                _physx2D.RemoveBody(obj.Body);

            return obj;
        }

        public IWorldObject2D? DestroyObject(IWorldObject2D obj)
            => obj is null ? null : DestroyObject(obj.InstanceId);

        // ── Position update ─────────────────────────────────────────────────────

        public async Task<IEnumerable<IWorldPartitionManager>> UpdateObjectPosition(IWorldObject2D obj)
        {
            if (obj is null)
                return Enumerable.Empty<IWorldPartitionManager>();

            // Remove from partitions only (keep in flat cache and physx)
            foreach (var p in _partitions)
                p.DestroyObject(obj.InstanceId);

            var partitions = FindPartitionsForObject(obj);
            AddObjectToPartitions(obj, partitions);
            return await Task.FromResult(partitions.ToList());
        }

        // ── Nearby queries ──────────────────────────────────────────────────────

        public IEnumerable<IWorldObject2D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y,
            float radius,
            string roomId)
        {
            var result = new List<IWorldObject2D>();
            var partitions = FindPartitionsForPosition(x, y, radius);

            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                    result.AddRange(p2d.GetObjectsByTypeInRadius(archetype, x, y, radius, roomId));
            }

            return result.Distinct();
        }

        // ── Partition queries ───────────────────────────────────────────────────

        public IWorldPartitionManager? FindPartitionForPosition(int x, int y)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);

            return _partitionMap.TryGetValue(new PartitionIndex2D(indexX, indexY), out var p) ? p : null;
        }

        public IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;

            return FindPartitionsForBounds(minX, minY, maxX, maxY);
        }

        public IEnumerable<IWorldPartitionManager> FindPartitionsForBounds(
            float minX, float minY,
            float maxX, float maxY)
        {
            return _partitions.Where(p =>
                maxX >= p.Position.X &&
                minX <= p.Position.X + p.Size.X &&
                maxY >= p.Position.Y &&
                minY <= p.Position.Y + p.Size.Y
            );
        }

        public IEnumerable<IWorldPartitionManager> FindPartitionsForObject(IWorldObject2D obj)
        {
            if (obj is null)
                return Enumerable.Empty<IWorldPartitionManager>();

            var (minX, minY, maxX, maxY) = GetObjectBounds(obj);
            return FindPartitionsForBounds(minX, minY, maxX, maxY);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static (float minX, float minY, float maxX, float maxY) GetObjectBounds(IWorldObject2D obj)
        {
            var pos = obj.Transform.Position;
            var size = obj.Transform.Size;

            bool degenerate =
                size.X == 0f && size.Y == 0f ||
                float.IsNaN(size.X) || float.IsNaN(size.Y) ||
                float.IsInfinity(size.X) || float.IsInfinity(size.Y);

            float halfX = degenerate ? 0.5f : size.X * 0.5f;
            float halfY = degenerate ? 0.5f : size.Y * 0.5f;

            return (pos.X - halfX, pos.Y - halfY, pos.X + halfX, pos.Y + halfY);
        }

        private static IEnumerable<IWorldPartitionManager> AddObjectToPartitions(
            IWorldObject2D obj,
            IEnumerable<IWorldPartitionManager> partitions)
        {
            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                    p2d.AddObject(obj);
            }

            return partitions;
        }
    }
}
