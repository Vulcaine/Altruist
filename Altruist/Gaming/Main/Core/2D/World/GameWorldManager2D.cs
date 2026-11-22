/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>One running 2D world: its <see cref="IWorldIndex2D"/>, physics world, spatial partitions,
    /// zones and the objects living in it. Created and stepped by <see cref="IGameWorldOrganizer2D"/>;
    /// get one with <see cref="IGameWorldOrganizer2D.GetWorld(int)"/> rather than constructing it.
    /// <para>Roles: the <b>organizer</b> owns all worlds and runs the tick (objects' <c>Step</c>, physics,
    /// AI, visibility, entity sync); the <b>manager</b> (this) owns one world's object set and spatial
    /// lookups; <see cref="IWorldLoader2D"/> builds a separate, standalone manager from a JSON world file (the
    /// 2D organizer does not call the loader, so configured <c>data-path</c>s are not loaded automatically). Use a world for
    /// persistent, partitioned spaces shared by many players; for short self-contained matches with
    /// their own fixed-rate simulation prefer the Rooms package. The 3D counterpart is
    /// <see cref="Altruist.Gaming.ThreeD.IGameWorldManager3D"/>.</para>
    /// <para>Threading: not thread-safe; call from the world tick (or marshal onto it).</para>
    /// <example><code>
    /// var world = organizer.GetWorld(0)!;
    /// var t = Transform2D.Zero.WithPosition(Position2D.Of(10, 5)).WithSize(Size2D.Of(1, 1));
    /// var body = await world.SpawnDynamicObject(new MyCrate(t)); // MyCrate : WorldObject2D, [WorldObject("crate")]
    /// var near = world.GetNearbyObjectsInRoom("crate", x, y, radius: 10, roomId: "");
    /// world.DestroyObject(crate);
    /// </code></example></summary>
    public interface IGameWorldManager2D : IGameWorldManager
    {
        /// <summary>The static description this world was built from.</summary>
        IWorldIndex2D Index { get; }
        /// <summary>The world's physics world (an <see cref="IPhysxWorld2D"/> for the default implementation).</summary>
        IPhysxWorld PhysxWorld { get; }
        /// <summary>The world's zones (named rectangular regions over the partitions), created lazily.</summary>
        IZoneManager2D Zones { get; }
        /// <summary>Computes the spatial partitions from <see cref="Index"/> and starts a background
        /// save of them to the cache. Called once by the organizer when the world is added.</summary>
        void Initialize();
        /// <summary>Saves every partition to the cache provider (no-op without one).</summary>
        Task SaveAsync();

        /// <summary>Looks an object up by the key it was spawned under (its <c>InstanceId</c> or the
        /// <c>withId</c> passed to spawn); null when absent.</summary>
        IWorldObject2D? FindObject(string id);
        /// <summary>All objects of type <typeparamref name="T"/> in the world (live view; do not spawn
        /// or destroy while enumerating).</summary>
        IEnumerable<T> FindAllObjects<T>() where T : IWorldObject2D;
        /// <summary>All objects in the world (live view; do not spawn or destroy while enumerating).</summary>
        IEnumerable<IWorldObject2D> GetAllObjects();

        /// <summary>Re-files <paramref name="obj"/> in the spatial partitions after its
        /// <c>Transform</c> changed (the organizer's physics sync does not do this). Object and body stay
        /// in the world.</summary>
        /// <returns>The partitions the object now belongs to.</returns>
        Task<IEnumerable<IWorldPartitionManager>> UpdateObjectPosition(IWorldObject2D obj);

        /// <summary>Adds a moving object: resolves its archetype from <see cref="WorldObjectAttribute"/>
        /// (kept as-is for <see cref="AnonymousWorldObject2D"/>), creates a dynamic body (mass 1) in this world at
        /// <c>obj.Transform</c>'s position and rotation, with one box collider of <c>obj.Transform.Size</c> (full
        /// extents; none when the size is zero) when body / collider API providers are available, assigns
        /// <c>obj.Body</c>, adds it to every partition its bounds overlap and indexes it.</summary>
        /// <param name="obj">The object to add; null is ignored.</param>
        /// <param name="withId">Optional lookup key for <see cref="FindObject"/> (default <c>obj.InstanceId</c>).</param>
        /// <returns>The created body, or null when no body API is registered (or <paramref name="obj"/> is null).</returns>
        Task<IPhysxBody2D?> SpawnDynamicObject(IWorldObject2D obj, string? withId = null);
        /// <summary>Adds a non-moving object (walls, props): like <see cref="SpawnDynamicObject"/> but with
        /// a static body (mass 0) and filed in the single partition at its position.</summary>
        /// <param name="obj">The object to add; null is ignored.</param>
        /// <param name="withId">Optional lookup key for <see cref="FindObject"/> (default <c>obj.InstanceId</c>).</param>
        /// <returns>The partition it was filed in, or null when none covers its position.</returns>
        IWorldPartitionManager? SpawnStaticObject(IWorldObject2D obj, string? withId = null);

        /// <summary>Legacy alias for <see cref="SpawnDynamicObject"/>.</summary>
        Task AddDynamicObject(IWorldObject2D obj);
        /// <summary>Legacy alias for <see cref="SpawnStaticObject"/>.</summary>
        IWorldPartitionManager? AddStaticObject(IWorldObject2D obj);

        /// <summary>Removes the object with this instance id (or spawn key) from the partitions and the
        /// index and removes its body from physics. To remove an object at the end of the current tick
        /// instead, set its <c>Expired</c> flag and let the organizer destroy it.</summary>
        /// <returns>The removed object, or null when not found.</returns>
        IWorldObject2D? DestroyObject(string instanceId);
        /// <summary><see cref="DestroyObject(string)"/> by <c>obj.InstanceId</c>; null for a null object.</summary>
        IWorldObject2D? DestroyObject(IWorldObject2D obj);

        /// <summary>Objects of one archetype within <paramref name="radius"/> of (<paramref name="x"/>,
        /// <paramref name="y"/>) whose <c>ZoneId</c> equals <paramref name="roomId"/> (exact match; pass
        /// <c>""</c> for objects without a zone). Uses the partitions' spatial grids, so positions are the
        /// ones last filed (see <see cref="UpdateObjectPosition"/>).</summary>
        /// <param name="archetype">Archetype to match (<see cref="WorldObjectAttribute"/> value).</param>
        /// <param name="x">Center X, world units.</param>
        /// <param name="y">Center Y, world units.</param>
        /// <param name="radius">Search radius, world units.</param>
        /// <param name="roomId">Zone / room id the objects must have.</param>
        IEnumerable<IWorldObject2D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y,
            float radius,
            string roomId);

        /// <summary>Partitions overlapping the square of half size <paramref name="radius"/> around
        /// (<paramref name="x"/>, <paramref name="y"/>).</summary>
        IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius);
        /// <summary>The partition containing (<paramref name="x"/>, <paramref name="y"/>), or null outside the world.</summary>
        IWorldPartitionManager? FindPartitionForPosition(int x, int y);

        /// <summary>Find all partitions whose AABB intersects the provided bounds (edges inclusive, world units).</summary>
        IEnumerable<IWorldPartitionManager> FindPartitionsForBounds(
            float minX, float minY,
            float maxX, float maxY);

        /// <summary>Find all partitions that intersect the bounds of the given object: centered on
        /// <c>Transform.Position</c> with full extents <c>Transform.Size</c> (1 x 1 when the size is zero or not finite).</summary>
        IEnumerable<IWorldPartitionManager> FindPartitionsForObject(IWorldObject2D obj);
    }

    /// <summary>Default <see cref="IGameWorldManager2D"/>: partitions from an <see cref="IWorldPartitioner2D"/>,
    /// objects in a flat dictionary plus per-partition <see cref="SpatialGridIndex2D"/>s, bodies through the
    /// optional body / collider API providers. Not a DI service: <see cref="GameWorldOrganizer2D"/> creates one
    /// per configured world.</summary>
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
        private ZoneManager2D? _zoneManager;

        /// <summary>Creates the manager; call <see cref="Initialize"/> before use (the organizer does).</summary>
        /// <param name="world">The world description.</param>
        /// <param name="physx2D">The physics world bodies are added to.</param>
        /// <param name="worldPartitioner">Computes the partition grid.</param>
        /// <param name="cacheProvider">Optional cache the partitions are saved to.</param>
        /// <param name="bodyApi">Optional body factory; without it spawned objects get no body.</param>
        /// <param name="colliderApi">Optional collider factory; without it bodies get no collider.</param>
        /// <exception cref="ArgumentNullException"><paramref name="world"/>, <paramref name="physx2D"/> or <paramref name="worldPartitioner"/> is null.</exception>
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

        /// <inheritdoc/>
        public IPhysxWorld PhysxWorld => _physx2D;
        /// <inheritdoc/>
        public IWorldIndex2D Index => _index;
        /// <inheritdoc/>
        public IZoneManager2D Zones => _zoneManager ??= new ZoneManager2D(_worldPartitioner, _partitions);

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public async Task SaveAsync()
        {
            if (_cache is null)
                return;
            var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.StorageId, p));
            await Task.WhenAll(saveTasks);
        }

        // ── Lookup ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IWorldObject2D? FindObject(string id)
            => _flatInstanceCache.TryGetValue(id, out var obj) ? obj : null;

        /// <inheritdoc/>
        public IEnumerable<T> FindAllObjects<T>() where T : IWorldObject2D
            => _flatInstanceCache.Values.OfType<T>();

        /// <inheritdoc/>
        public IEnumerable<IWorldObject2D> GetAllObjects()
            => _flatInstanceCache.Values;

        // ── Spawn ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task<IPhysxBody2D?> SpawnDynamicObject(IWorldObject2D obj, string? withId = null)
        {
            if (obj is null)
                return null;

            obj.ObjectArchetype = obj is AnonymousWorldObject2D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper2D.ResolveArchetype(obj.GetType());

            var body = CreateBodyFor(obj, PhysxBodyType.Dynamic, mass: 1f);

            var partitions = FindPartitionsForObject(obj);
            foreach (var p in partitions)
                if (p is WorldPartition2D p2d) p2d.AddObject(obj);

            _flatInstanceCache[withId ?? obj.InstanceId] = obj;

            await Task.CompletedTask;
            return body;
        }

        /// <inheritdoc/>
        public IWorldPartitionManager? SpawnStaticObject(IWorldObject2D obj, string? withId = null)
        {
            if (obj is null)
                return null;

            obj.ObjectArchetype = obj is AnonymousWorldObject2D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper2D.ResolveArchetype(obj.GetType());

            CreateBodyFor(obj, PhysxBodyType.Static, mass: 0f);

            var partition = FindPartitionForPosition(
                (int)MathF.Floor(obj.Transform.Position.X),
                (int)MathF.Floor(obj.Transform.Position.Y));

            if (partition is WorldPartition2D p2d)
                p2d.AddObject(obj);

            _flatInstanceCache[withId ?? obj.InstanceId] = obj;

            return partition;
        }

        // A body in this world at the object's position and rotation, with one box collider of the object's
        // size (Transform.Size is the full extent; the box sits at the body origin). No collider for an
        // object without a positive size. Null without a body API.
        private IPhysxBody2D? CreateBodyFor(IWorldObject2D obj, PhysxBodyType type, float mass)
        {
            if (_bodyApi is null)
                return null;

            var body = _bodyApi.CreateBody(_physx2D, type, mass, obj.Transform);
            var size = obj.Transform.Size;
            if (_colliderApi is not null && size.X > 0f && size.Y > 0f)
            {
                var local = new Transform2D(Position2D.Zero, Size2D.Of(size.X * 0.5f, size.Y * 0.5f), Scale2D.One, Rotation2D.Zero);
                _bodyApi.AddCollider(body, _colliderApi.CreateCollider(
                    new PhysxCollider2DParams(PhysxColliderShape2D.Box2D, local, isTrigger: false)));
            }

            obj.Body = body;
            return body;
        }

        // ── Legacy aliases ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task AddDynamicObject(IWorldObject2D obj)
            => await SpawnDynamicObject(obj);

        /// <inheritdoc/>
        public IWorldPartitionManager? AddStaticObject(IWorldObject2D obj)
            => SpawnStaticObject(obj);

        // ── Destroy ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public IWorldObject2D? DestroyObject(IWorldObject2D obj)
            => obj is null ? null : DestroyObject(obj.InstanceId);

        // ── Position update ─────────────────────────────────────────────────────

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public IWorldPartitionManager? FindPartitionForPosition(int x, int y)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);

            return _partitionMap.TryGetValue(new PartitionIndex2D(indexX, indexY), out var p) ? p : null;
        }

        /// <inheritdoc/>
        public IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;

            return FindPartitionsForBounds(minX, minY, maxX, maxY);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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
