/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// One persistent 3D world (open world / shard): owns the world's objects, its spatial
    /// partitions, its zones and (optionally) a physics world. Objects are added with the
    /// <c>Spawn*</c> methods and removed with <see cref="DestroyObject(string)"/>; the
    /// <see cref="GameWorldOrganizer3D"/> steps every registered world each engine frame
    /// (object <c>Step</c>, physics step, physics-to-transform sync, AI, visibility, entity sync).
    /// </summary>
    /// <remarks>
    /// Use a world for long-lived shared spaces with spatial queries and visibility. For short,
    /// fixed-rate matches use the Rooms package instead. For 2D games use
    /// <see cref="Altruist.Gaming.TwoD.IGameWorldManager2D"/>. Instances are created by
    /// <see cref="IWorldLoader3D"/> (one per configured <see cref="IWorldIndex3D"/>) and looked up via
    /// <see cref="IGameWorldOrganizer3D.GetWorld(int)"/>; you normally do not construct one yourself.
    /// Not thread-safe for concurrent writers: call spawn/destroy from the engine/world step thread
    /// (the object cache is concurrent, the partitions are not).
    /// </remarks>
    /// <example>
    /// <code>
    /// var world = organizer.GetWorld(0)!;
    /// await world.SpawnDynamicObject(myObject);
    /// var near = world.GetNearbyObjectsInRoom("enemy", x, y, z, radius: 30f, roomId: "");
    /// world.DestroyObject(myObject);
    /// </code>
    /// </example>
    public interface IGameWorldManager3D : IGameWorldManager
    {
        /// <summary>The configuration this world was created from (index, name, size, gravity, data path).</summary>
        IWorldIndex3D Index { get; }
        /// <summary>
        /// The physics world, or <c>null</c> when the world runs without a physics engine
        /// (objects then only get in-memory bodies, see <see cref="SpawnDynamicObject"/>).
        /// </summary>
        IPhysxWorld3D? PhysxWorld { get; }
        /// <summary>Zone registry for this world (named AABB regions); created lazily on first access.</summary>
        IZoneManager3D Zones { get; }

        /// <summary>
        /// Re-files <paramref name="obj"/> into the partitions that its current bounds intersect after it moved.
        /// The physics body is left untouched.
        /// </summary>
        /// <remarks>
        /// The object stays in the world: <see cref="FindObject"/>, <see cref="GetAllObjects"/> and
        /// <see cref="GetCachedSnapshot"/> keep returning it.
        /// </remarks>
        /// <param name="obj">The object whose position changed; <c>null</c> returns an empty sequence.</param>
        /// <returns>The partitions the object is now registered in.</returns>
        Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj);

        /// <summary>Looks up a live object by the key it was spawned with (its <c>InstanceId</c>, or the <c>withId</c> override).</summary>
        /// <param name="id">Instance id / spawn key.</param>
        /// <returns>The object, or <c>null</c> when not present.</returns>
        IWorldObject3D? FindObject(string id);
        /// <summary>
        /// Enumerates all live objects assignable to <typeparamref name="T"/> (filtered with <c>OfType</c>, allocates per call).
        /// For per-tick iteration over everything prefer <see cref="GetCachedSnapshot"/>.
        /// </summary>
        /// <typeparam name="T">World object type to filter by.</typeparam>
        IEnumerable<T> FindAllObjects<T>() where T : IWorldObject3D;
        /// <summary>Enumerates all live objects (a live view over the concurrent instance cache; order unspecified).</summary>
        IEnumerable<IWorldObject3D> GetAllObjects();
        /// <summary>
        /// Returns a cached list + id lookup of all live objects, rebuilt only when objects were
        /// spawned or destroyed since the last call. Used once per tick by the organizer to feed
        /// AI, visibility and entity sync without re-materializing the object set.
        /// </summary>
        /// <remarks>
        /// The returned collections are reused and mutated in place on the next rebuild; do not hold them
        /// across ticks and do not call this concurrently. Use <see cref="SnapshotVersion"/> to detect rebuilds.
        /// The lookup is keyed by <c>InstanceId</c>.
        /// </remarks>
        (IReadOnlyList<IWorldObject3D> List, IReadOnlyDictionary<string, IWorldObject3D> Lookup) GetCachedSnapshot();

        /// <summary>
        /// Monotonically increments every time <see cref="GetCachedSnapshot"/> rebuilds
        /// the underlying list. Callers that cache derived structures keyed by the
        /// snapshot's indices (e.g. spatial grids) must invalidate when this changes —
        /// the snapshot's List reference is stable across rebuilds, so reference
        /// equality cannot detect when the contents were swapped.
        /// </summary>
        int SnapshotVersion { get; }
        /// <summary>
        /// Spawns <paramref name="obj"/>, picking the cheapest path: a dynamic physics body
        /// (<see cref="SpawnDynamicObject"/>) when the object has a <c>BodyDescriptor</c> or at least one
        /// non-trigger collider descriptor, otherwise <see cref="SpawnLightweight"/> (no body).
        /// </summary>
        /// <remarks>Use this when you do not care which path is taken; call the specific method to force one.</remarks>
        /// <param name="obj">The object to register.</param>
        /// <param name="withId">Optional key for the instance cache instead of <c>obj.InstanceId</c>.</param>
        /// <returns>The created body, or <c>null</c> for the lightweight path.</returns>
        Task<IPhysxBody3D?> SpawnObject(IWorldObject3D obj, string? withId = null);
        /// <summary>
        /// Spawns <paramref name="obj"/> with a dynamic physics body. With a physics engine the body is built from
        /// <c>obj.BodyDescriptor</c> (default: dynamic, mass 1 at <c>obj.Transform</c>) and <c>obj.ColliderDescriptors</c>
        /// (default: one non-trigger box at <c>obj.Transform</c>), added to <see cref="PhysxWorld"/> and written back to
        /// <c>Body</c>/<c>Colliders</c>. Without a physics engine an in-memory body (position/velocity only) is attached.
        /// </summary>
        /// <remarks>
        /// Also assigns <c>VirtualId</c> (process-wide sequence) if 0, resolves <c>ObjectArchetype</c> from
        /// <see cref="WorldObjectAttribute"/> (except for <see cref="AnonymousWorldObject3D"/>), files the object into
        /// partitions, marks the snapshot dirty and raises <see cref="OnObjectCreated"/> synchronously.
        /// Use <see cref="SpawnStaticObject"/> for immovable geometry and <see cref="SpawnLightweight"/> for objects that need no collision.
        /// </remarks>
        /// <param name="obj">The object to register.</param>
        /// <param name="withId">Optional key for the instance cache instead of <c>obj.InstanceId</c>.</param>
        /// <returns>The created body, or <c>null</c> when <paramref name="obj"/> is <c>null</c>.</returns>
        Task<IPhysxBody3D?> SpawnDynamicObject(IWorldObject3D obj, string? withId = null);
        /// <summary>
        /// Spawns <paramref name="obj"/> as static (immovable, mass 0) geometry such as terrain or buildings.
        /// Same registration steps as <see cref="SpawnDynamicObject"/>; without a physics engine no body is created at all.
        /// </summary>
        /// <param name="obj">The object to register.</param>
        /// <param name="withId">Optional key for the instance cache instead of <c>obj.InstanceId</c>.</param>
        /// <returns>The created static body, or <c>null</c> when physics is disabled.</returns>
        Task<IPhysxBody3D?> SpawnStaticObject(IWorldObject3D obj, string? withId = null);

        /// <summary>
        /// Spawn an object into the world without creating a physics body.
        /// The object is tracked in partitions for spatial queries and visibility,
        /// but has no collision or physics simulation. Ideal for distance-based
        /// combat entities that only need position tracking.
        /// </summary>
        /// <remarks>Assigns <c>VirtualId</c>/archetype and raises <see cref="OnObjectCreated"/> like the other spawn methods.</remarks>
        /// <param name="obj">The object to register; <c>null</c> is ignored.</param>
        /// <param name="withId">Optional key for the instance cache instead of <c>obj.InstanceId</c>.</param>
        void SpawnLightweight(IWorldObject3D obj, string? withId = null);
        /// <summary>
        /// Removes the object with the given instance id from partitions and the instance cache, removes its body from the
        /// physics engine and raises <see cref="OnObjectDestroyed"/> synchronously. For deferred removal during a tick,
        /// set <c>Expired = true</c> on the object instead; the organizer destroys expired objects at the start of the next world step.
        /// </summary>
        /// <param name="instanceId">Instance id (or the <c>withId</c> key used at spawn).</param>
        /// <returns>The removed object, or <c>null</c> when nothing matched.</returns>
        IWorldObject3D? DestroyObject(string instanceId);
        /// <summary>Destroys <paramref name="obj"/> by its <c>InstanceId</c>; see <see cref="DestroyObject(string)"/>.</summary>
        /// <param name="obj">The object to remove; <c>null</c> returns <c>null</c>.</param>
        /// <returns>The removed object, or <c>null</c> when nothing matched.</returns>
        IWorldObject3D? DestroyObject(IWorldObject3D obj);

        /// <summary>
        /// Fires synchronously whenever DestroyObject successfully removes an object.
        /// Subscribers (VisibilityTracker3D) use this to broadcast invisibility to
        /// observers that were seeing the object — independent of the snapshot rebuild
        /// timing, so there's no race between gate-handler-driven destroys and the
        /// per-tick visibility Tick.
        /// </summary>
        event Action<IWorldObject3D>? OnObjectDestroyed;

        /// <summary>
        /// Raised synchronously after a world object is registered (spawn/attach/lightweight).
        /// Subscribers (VisibilityTracker3D) use this to broadcast visibility to in-range
        /// observers without waiting for the next per-tick Tick. Out-of-range observers
        /// are still picked up by the regular Tick when they move into range.
        /// </summary>
        event Action<IWorldObject3D>? OnObjectCreated;

        /// <summary>
        /// Returns objects of one archetype whose position lies within <paramref name="radius"/> of a point
        /// and whose <c>ZoneId</c> equals <paramref name="roomId"/> (ordinal), using the partitions' spatial grids.
        /// </summary>
        /// <remarks>
        /// Distance is a full 3D sphere test against <c>Transform.Position</c> (object extents are ignored).
        /// Only objects currently filed in partitions are found. For physics-shape queries use the 3D query/physics APIs instead.
        /// </remarks>
        /// <param name="archetype">Archetype to match exactly (as resolved from <see cref="WorldObjectAttribute"/>; <c>""</c> for none).</param>
        /// <param name="x">Query center X (world units).</param>
        /// <param name="y">Query center Y (world units).</param>
        /// <param name="z">Query center Z (world units).</param>
        /// <param name="radius">Sphere radius in world units.</param>
        /// <param name="roomId">Zone/room id to match; pass <c>""</c> for objects without a zone.</param>
        /// <returns>Distinct matching objects (order unspecified).</returns>
        IEnumerable<IWorldObject3D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId);

        /// <summary>
        /// Find all partitions intersecting a sphere around a position.
        /// Useful for "what partitions does this player see?" style queries.
        /// </summary>
        /// <remarks>The sphere is approximated by its bounding cube (see <see cref="FindPartitionsForBounds"/>).</remarks>
        /// <param name="x">Center X (world units).</param>
        /// <param name="y">Center Y (world units).</param>
        /// <param name="z">Center Z (world units).</param>
        /// <param name="radius">Radius in world units.</param>
        /// <returns>A lazily evaluated sequence of intersecting partitions.</returns>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius);

        /// <summary>
        /// Find a single partition that contains a specific position (if any).
        /// </summary>
        /// <remarks>
        /// The cell index is <c>floor(coord / partitionSize)</c>, so a position belongs to the partition whose
        /// half-open range <c>[Position, Position + Size)</c> contains it. Use <see cref="FindPartitionsForBounds"/> /
        /// <see cref="FindPartitionsForPosition"/> for every partition an area overlaps.
        /// </remarks>
        /// <param name="x">X (world units).</param>
        /// <param name="y">Y (world units).</param>
        /// <param name="z">Z (world units).</param>
        /// <returns>The partition, or <c>null</c> if the index is outside the world grid.</returns>
        WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z);

        /// <summary>
        /// Find all partitions whose AABB intersects the provided bounds.
        /// </summary>
        /// <remarks>Inclusive test (touching faces count). Partition bounds start at the world origin (0,0,0); <c>IWorldIndex3D.Position</c> is not applied.</remarks>
        /// <param name="minX">Minimum X of the query box.</param>
        /// <param name="minY">Minimum Y of the query box.</param>
        /// <param name="minZ">Minimum Z of the query box.</param>
        /// <param name="maxX">Maximum X of the query box.</param>
        /// <param name="maxY">Maximum Y of the query box.</param>
        /// <param name="maxZ">Maximum Z of the query box.</param>
        /// <returns>A lazily evaluated sequence of intersecting partitions.</returns>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForBounds(
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ);

        /// <summary>
        /// Find all partitions that intersect the bounds of the given object.
        /// </summary>
        /// <remarks>
        /// Bounds come from the first non-trigger collider descriptor (heightfield colliders use their full grid extent),
        /// else a heightfield collider, else <c>obj.Transform</c> position ± size/2. A zero/NaN collider size falls back to the transform size.
        /// </remarks>
        /// <param name="obj">The object; <c>null</c> returns an empty sequence.</param>
        /// <returns>A lazily evaluated sequence of intersecting partitions.</returns>
        IEnumerable<WorldPartitionManager3D> FindPartitionsForObject(IWorldObject3D obj);
    }

    /// <summary>
    /// Default <see cref="IGameWorldManager3D"/>: partitions the world with an <see cref="IWorldPartitioner3D"/>,
    /// keeps objects in a concurrent instance cache plus per-partition spatial grids, and creates physics bodies
    /// through the optional body/collider API providers.
    /// </summary>
    /// <remarks>
    /// Not a DI service: <see cref="WorldLoader3D"/> constructs one per world index. Physics is enabled when a
    /// non-null <see cref="IPhysxWorld3D"/> is passed; bodies are only created when the body and collider API
    /// providers are also supplied.
    /// </remarks>
    public sealed class GameWorldManager3D : IGameWorldManager3D
    {
        private readonly IWorldIndex3D _index;
        private readonly IWorldPartitioner3D _worldPartitioner;
        private readonly Dictionary<PartitionIndex3D, WorldPartitionManager3D> _partitionMap = new();

        private readonly List<WorldPartitionManager3D> _partitions;
        private readonly IPhysxWorld3D? _physx3D;
        private readonly bool _physicsEnabled;

        private readonly IPhysxBodyApiProvider3D? _bodyApi;
        private readonly IPhysxColliderApiProvider3D? _colliderApi;

        private static uint _nextVirtualId = 1;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IWorldObject3D> _flatInstanceCache = new();
        private ZoneManager3D? _zoneManager;

        // ── Snapshot caching ─────────────────────────────────────────
        private readonly List<IWorldObject3D> _snapshotCache = new();
        private readonly Dictionary<string, IWorldObject3D> _snapshotLookup = new();
        private volatile bool _snapshotDirty = true;
        private int _snapshotVersion;

        /// <inheritdoc/>
        public event Action<IWorldObject3D>? OnObjectDestroyed;
        /// <inheritdoc/>
        public event Action<IWorldObject3D>? OnObjectCreated;

        /// <inheritdoc/>
        public int SnapshotVersion => _snapshotVersion;

        /// <inheritdoc/>
        public (IReadOnlyList<IWorldObject3D> List, IReadOnlyDictionary<string, IWorldObject3D> Lookup) GetCachedSnapshot()
        {
            if (_snapshotDirty)
            {
                // Clear dirty BEFORE iterating so concurrent MarkSnapshotDirty()
                // calls during iteration will set it back to true, triggering
                // a rebuild on the next tick instead of being lost.
                _snapshotDirty = false;
                _snapshotCache.Clear();
                _snapshotLookup.Clear();
                foreach (var kvp in _flatInstanceCache)
                {
                    _snapshotCache.Add(kvp.Value);
                    _snapshotLookup[kvp.Value.InstanceId] = kvp.Value;
                }
                _snapshotVersion++;
            }
            return (_snapshotCache, _snapshotLookup);
        }

        private void MarkSnapshotDirty() => _snapshotDirty = true;

        /// <summary>Creates the manager and immediately computes the world's partitions (see <see cref="Initialize"/>).</summary>
        /// <param name="world">World configuration (size drives the partition grid).</param>
        /// <param name="physx3D">Physics world, or <c>null</c> to run without physics.</param>
        /// <param name="worldPartitioner">Partitioner that splits the world into partitions.</param>
        /// <param name="bodyApi">Body factory; required (with <paramref name="colliderApi"/>) for real physics bodies.</param>
        /// <param name="colliderApi">Collider factory; required (with <paramref name="bodyApi"/>) for real physics bodies.</param>
        public GameWorldManager3D(
            IWorldIndex3D world,
            IPhysxWorld3D? physx3D,
            IWorldPartitioner3D worldPartitioner,
            IPhysxBodyApiProvider3D? bodyApi = null,
            IPhysxColliderApiProvider3D? colliderApi = null
        )
        {
            _index = world;
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _worldPartitioner = worldPartitioner;
            _physicsEnabled = physx3D != null;

            _physx3D = physx3D;
            _partitions = new List<WorldPartitionManager3D>();
            Initialize();
        }

        /// <inheritdoc/>
        public IPhysxWorld3D? PhysxWorld => _physx3D;
        /// <inheritdoc/>
        public IWorldIndex3D Index => _index;
        /// <inheritdoc/>
        public IZoneManager3D Zones => _zoneManager ??= new ZoneManager3D(_worldPartitioner, _partitions);

        /// <summary>
        /// Computes the partition grid from <see cref="Index"/> and registers the partitions. Called by the constructor;
        /// calling it again appends a duplicate set of partitions, so do not call it manually.
        /// </summary>
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
        /// Recalculate which partitions contain the given object, based on its bounds. The object stays in the
        /// world: its body, its <see cref="FindObject"/> entry and the snapshot are untouched.
        /// </summary>
        public async Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj)
        {
            if (obj is null)
                return Enumerable.Empty<WorldPartitionManager3D>();

            for (int i = 0; i < _partitions.Count; i++)
                _partitions[i].DestroyObject(obj.InstanceId);

            var partitions = FindPartitionsForObject(obj);
            AddObjectToPartitions(obj, partitions);

            return await Task.FromResult(partitions.ToList());
        }

        /// <inheritdoc/>
        public Task<IPhysxBody3D?> SpawnObject(IWorldObject3D obj, string? withId = null)
        {
            if (RequiresPhysicsBody(obj))
                return SpawnDynamicObject(obj, withId);

            SpawnLightweight(obj, withId);
            return Task.FromResult<IPhysxBody3D?>(null);
        }

        /// <inheritdoc/>
        public async Task<IPhysxBody3D?> SpawnDynamicObject(IWorldObject3D obj, string? withId = null)
        {
            return await SpawnObjectInternal(
                obj,
                bodyType: PhysxBodyType.Dynamic,
                isStatic: false,
                withId: withId);
        }

        /// <inheritdoc/>
        public async Task<IPhysxBody3D?> SpawnStaticObject(IWorldObject3D obj, string? withId = null)
        {
            return await SpawnObjectInternal(
                obj,
                bodyType: PhysxBodyType.Static,
                isStatic: true,
                withId: withId);
        }

        /// <inheritdoc/>
        public void SpawnLightweight(IWorldObject3D obj, string? withId = null)
        {
            if (obj is null) return;

            EnsureSpawnMetadata(obj);

            // Add to partitions for spatial queries — no physics body
            var partitions = FindPartitionsForObject(obj);
            AddObjectToPartitions(obj, partitions);

            if (withId != null)
                _flatInstanceCache[withId] = obj;
            else
                _flatInstanceCache[obj.InstanceId] = obj;

            MarkSnapshotDirty();
            OnObjectCreated?.Invoke(obj);
        }

        /// <summary>
        /// Core spawn logic shared by dynamic &amp; static world objects.
        /// </summary>
        private async Task<IPhysxBody3D?> SpawnObjectInternal(
            IWorldObject3D obj,
            PhysxBodyType bodyType,
            bool isStatic,
            string? withId = null)
        {
            if (obj is null)
                return null;

            EnsureSpawnMetadata(obj);

            IPhysxBody3D? body = null;

            // Only create physics bodies when physics is enabled
            if (_physicsEnabled && _physx3D != null && _bodyApi != null && _colliderApi != null)
            {
                var engine3D = _physx3D.Engine;
                var mass = isStatic ? 0f : 1f;

                var bodyDesc = obj.BodyDescriptor ?? PhysxBody3D.Create(
                    bodyType, mass: mass, transform: obj.Transform);

                var colliderDescs = obj.ColliderDescriptors;
                if (colliderDescs == null || !colliderDescs.Any())
                {
                    colliderDescs =
                    [
                        PhysxCollider3D.Create(
                            PhysxColliderShape3D.Box3D,
                            obj.Transform,
                            isTrigger: false)
                    ];
                }

                body = _bodyApi.CreateBody(engine3D, bodyDesc);

                var createdColliders = new List<IPhysxCollider3D>();
                foreach (var cd in colliderDescs)
                {
                    var collider = _colliderApi.CreateCollider(cd);
                    _bodyApi.AddCollider(engine3D, body, collider);
                    createdColliders.Add(collider);
                }

                obj.BodyDescriptor = bodyDesc;
                obj.Body = body;
                obj.Body.PhysxTag = bodyDesc.PhysxTag ?? new PhysxTag((uint)PhysxLayer.All);
                obj.ColliderDescriptors = colliderDescs;
                obj.Colliders = createdColliders;

                _physx3D.AddBody(body);
            }
            else if (!isStatic)
            {
                var bodyDesc = obj.BodyDescriptor ?? PhysxBody3D.Create(
                    bodyType, mass: 1f, transform: obj.Transform, isKinematic: bodyType == PhysxBodyType.Kinematic);

                body = new InMemoryPhysxBody3D(bodyDesc);
                obj.BodyDescriptor = bodyDesc;
                obj.Body = body;
            }

            // Always register in partitions for spatial queries
            var partitions = FindPartitionsForObject(obj);
            foreach (var p in partitions)
                p.AddObject(obj);

            if (withId != null)
                _flatInstanceCache[withId] = obj;
            else
                _flatInstanceCache[obj.InstanceId] = obj;

            MarkSnapshotDirty();
            OnObjectCreated?.Invoke(obj);
            await Task.CompletedTask;
            return body;
        }

        private static bool RequiresPhysicsBody(IWorldObject3D? obj)
        {
            if (obj?.BodyDescriptor != null)
                return true;

            var colliders = obj?.ColliderDescriptors;
            return colliders != null && colliders.Any(c => !c.IsTrigger);
        }

        private static void EnsureSpawnMetadata(IWorldObject3D obj)
        {
            if (obj.VirtualId == 0)
                obj.VirtualId = Interlocked.Increment(ref _nextVirtualId);

            obj.ObjectArchetype = obj is AnonymousWorldObject3D
                ? obj.ObjectArchetype
                : WorldObjectArchetypeHelper.ResolveArchetype(obj.GetType());
        }

        /// <inheritdoc/>
        public IWorldObject3D? DestroyObject(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return null;

            // Destroy = detach + remove from PhysX engine
            return DetachObjectInternal(instanceId);
        }

        /// <inheritdoc/>
        public IWorldObject3D? DestroyObject(IWorldObject3D obj)
            => obj is null ? null : DestroyObject(obj.InstanceId);

        /// <summary>
        /// Removes the object from the partitions, the instance cache and the physics engine.
        /// </summary>
        private IWorldObject3D? DetachObjectInternal(string instanceId)
        {
            // Remove from partitions — use for-loop instead of LINQ to avoid allocations
            IWorldObject3D? removedFromPartitions = null;
            for (int i = 0; i < _partitions.Count; i++)
            {
                var removed = _partitions[i].DestroyObject(instanceId);
                if (removed != null && removedFromPartitions == null)
                    removedFromPartitions = removed;
            }

            IWorldObject3D? removedFromCache = null;

            if (_flatInstanceCache.TryRemove(instanceId, out var cachedByKey))
            {
                removedFromCache = cachedByKey;
            }

            var obj = removedFromPartitions ?? removedFromCache;

            if (obj != null)
            {
                MarkSnapshotDirty();
                RemoveFromPhysxEngine(obj);
                OnObjectDestroyed?.Invoke(obj);
            }

            return obj;
        }

        private void RemoveFromPhysxEngine(IWorldObject3D obj)
        {
            if (!_physicsEnabled || _physx3D == null) return;
            var body = obj.Body;
            if (body != null)
            {
                _physx3D.Engine.RemoveBody(body);
            }
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z)
        {
            int indexX = (int)Math.Floor(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Floor(y / (double)_worldPartitioner.PartitionHeight);
            int indexZ = (int)Math.Floor(z / (double)_worldPartitioner.PartitionDepth);

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
            PhysxCollider3DDesc? heightfieldCollider = null;

            var colliders = obj.ColliderDescriptors;
            if (colliders != null)
            {
                foreach (var c in colliders)
                {
                    if (c.Heightfield is not null && !heightfieldCollider.HasValue)
                        heightfieldCollider = c;

                    if (!c.IsTrigger)
                    {
                        chosen = c;
                        break;
                    }
                }
            }

            Transform3D transformToUse;

            var colliderForBounds = chosen ?? heightfieldCollider;
            if (colliderForBounds.HasValue)
            {
                transformToUse = colliderForBounds.Value.Transform;

                if (colliderForBounds.Value.Heightfield is { } heightfield)
                {
                    var p = transformToUse.Position;
                    var width = MathF.Max(1f, (heightfield.Width - 1) * heightfield.CellSizeX);
                    var depth = MathF.Max(1f, (heightfield.Height - 1) * heightfield.CellSizeZ);

                    var heightfieldMinY = p.Y;
                    var heightfieldMaxY = p.Y;
                    for (var x = 0; x < heightfield.Width; x++)
                    {
                        for (var z = 0; z < heightfield.Height; z++)
                        {
                            var y = p.Y + heightfield.Heights[x, z];
                            if (y < heightfieldMinY) heightfieldMinY = y;
                            if (y > heightfieldMaxY) heightfieldMaxY = y;
                        }
                    }

                    return (p.X, heightfieldMinY, p.Z, p.X + width, heightfieldMaxY, p.Z + depth);
                }
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

        /// <inheritdoc/>
        public IWorldObject3D? FindObject(string id)
            => _flatInstanceCache.TryGetValue(id, out var obj) ? obj : null;

        /// <inheritdoc/>
        public IEnumerable<T> FindAllObjects<T>() where T : IWorldObject3D
        {
            return _flatInstanceCache.Values
                .OfType<T>();
        }

        /// <inheritdoc/>
        public IEnumerable<IWorldObject3D> GetAllObjects()
        {
            return _flatInstanceCache.Values;
        }
    }
}
