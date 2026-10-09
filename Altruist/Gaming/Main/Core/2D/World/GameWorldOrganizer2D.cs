/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>Owns every 2D world (<see cref="IGameWorldManager2D"/>) of the server and advances them each
    /// engine frame. Inject it to find a world (<see cref="GetWorld(int)"/> / <see cref="GetWorld(string)"/>)
    /// or to add one at runtime. Registered only when <c>altruist:environment:mode</c> is <c>2D</c>; the 3D
    /// counterpart is <see cref="Altruist.Gaming.ThreeD.IGameWorldOrganizer3D"/>.
    /// <para>Despite inheriting <see cref="IGameWorldOrganizer"/>, the default implementation is not the
    /// engine's top-level organizer: it is an <see cref="IWorldStepper"/> stepped by
    /// <see cref="WorldCoordinator"/>.</para>
    /// <example><code>
    /// public sealed class SpawnService(IGameWorldOrganizer2D worlds)
    /// {
    ///     public Task Spawn(IWorldObject2D obj) =&gt; worlds.GetWorld(0)!.SpawnDynamicObject(obj);
    /// }
    /// </code></example></summary>
    public interface IGameWorldOrganizer2D : IGameWorldOrganizer
    {
        /// <summary>Creates, initializes and registers a world for <paramref name="index"/> backed by
        /// <paramref name="physx2D"/>.</summary>
        /// <returns>The new world manager.</returns>
        /// <exception cref="InvalidOperationException">A world with the same <c>Index.Index</c> already exists.</exception>
        IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D);
        /// <summary>Unregisters the world with this index (no-op when absent). Its objects and physics are not torn down.</summary>
        void RemoveWorld(int index);
        /// <summary>The world with this numeric index, or null.</summary>
        IGameWorldManager2D? GetWorld(int index);
        /// <summary>The first world whose <c>Index.Name</c> equals <paramref name="name"/> (ordinal), or null.</summary>
        IGameWorldManager2D? GetWorld(string name);
        /// <summary>All registered worlds.</summary>
        IEnumerable<IGameWorldManager2D> GetAllWorlds();
        /// <summary>Sets (or clears) the visibility tracker ticked after physics each step. Wired after
        /// construction because the tracker itself depends on the organizer.</summary>
        void SetVisibilityTracker(IVisibilityTracker? tracker);
    }

    /// <summary>Default <see cref="IGameWorldOrganizer2D"/>, registered as a singleton service and as an
    /// <see cref="IWorldStepper"/> (variable mode) when <c>altruist:environment:mode</c> is <c>2D</c>. On
    /// construction it creates one <see cref="GameWorldManager2D"/> per configured <see cref="IWorldIndex2D"/>
    /// (each with its own physics engine from <see cref="IPhysxWorldEngineFactory2D"/>).
    /// <para>Each <see cref="Step"/>, per world: destroys <c>Expired</c> objects and calls every object's
    /// <c>Step(dt, world)</c>; then steps each physics world once with the frame's <c>dt</c>; copies body
    /// positions back to <c>Transform.Position</c> (truncated to integers; partitions are not re-filed);
    /// then ticks <see cref="IAIBehaviorService"/>, the <see cref="VisibilityTracker2D"/> and
    /// <see cref="IEntitySyncService"/> (at <see cref="EntitySyncHz"/>). Exceptions from objects and services
    /// are swallowed so one failure does not stop the tick.</para>
    /// <para>Stepped by the WorldCoordinator (the engine's IGameWorldOrganizer) with the variable frame time.</para></summary>
    [Service(typeof(IWorldStepper))]
    [Service(typeof(IGameWorldOrganizer2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public class GameWorldOrganizer2D : IGameWorldOrganizer2D, IWorldStepper
    {
        private readonly Dictionary<int, IGameWorldManager2D> _worlds = new();
        private readonly IWorldPartitioner2D _partitioner;
        private readonly ICacheProvider _cache;
        private readonly IPhysxWorldEngineFactory2D _physxWorldEngineFactory;
        private readonly IPhysxBodyApiProvider2D? _bodyApi;
        private readonly IPhysxColliderApiProvider2D? _colliderApi;
        private IVisibilityTracker? _visibilityTracker;
        private readonly IAIBehaviorService? _aiBehaviorService;
        private readonly IEntitySyncService? _entitySyncService;
        /// <summary>
        /// Rate the entity sync throttling ([Synchronized(Frequency)]) assumes for the world step:
        /// <c>altruist:game:worlds:entity-sync-hz</c> (default 25).
        /// </summary>
        private readonly float _engineFrequencyHz;

        /// <summary>Default for <c>altruist:game:worlds:entity-sync-hz</c>.</summary>
        public const float DefaultEntitySyncHz = 25f;

        /// <summary>The sync rate passed to the entity sync service (<c>altruist:game:worlds:entity-sync-hz</c>).</summary>
        public float EntitySyncHz => _engineFrequencyHz;

        /// <summary>DI constructor; builds and initializes a world for every configured index.</summary>
        /// <param name="partitioner">Partition grid for every world.</param>
        /// <param name="cache">Cache the partitions are saved to.</param>
        /// <param name="physxWorldEngineFactory">Creates each world's physics engine (gravity and fixed step from the index).</param>
        /// <param name="gameWorlds">Configured world descriptions (see <see cref="WorldIndex2D"/>).</param>
        /// <param name="bodyApi">Optional body factory passed to each world.</param>
        /// <param name="colliderApi">Optional collider factory passed to each world.</param>
        /// <param name="aiBehaviorService">Optional AI service ticked after physics.</param>
        /// <param name="entitySyncService">Optional entity sync ticked last.</param>
        /// <param name="entitySyncHz">Config <c>altruist:game:worlds:entity-sync-hz</c> (default 25; non-positive falls back to it).</param>
        public GameWorldOrganizer2D(
            IWorldPartitioner2D partitioner,
            ICacheProvider cache,
            IPhysxWorldEngineFactory2D physxWorldEngineFactory,
            IEnumerable<IWorldIndex2D> gameWorlds,
            IPhysxBodyApiProvider2D? bodyApi = null,
            IPhysxColliderApiProvider2D? colliderApi = null,
            IAIBehaviorService? aiBehaviorService = null,
            IEntitySyncService? entitySyncService = null,
            [AppConfigValue("altruist:game:worlds:entity-sync-hz", "25")] float entitySyncHz = DefaultEntitySyncHz)
        {
            _partitioner = partitioner;
            _cache = cache;
            _physxWorldEngineFactory = physxWorldEngineFactory;
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _aiBehaviorService = aiBehaviorService;
            _entitySyncService = entitySyncService;
            _engineFrequencyHz = entitySyncHz > 0 ? entitySyncHz : DefaultEntitySyncHz;
            _worlds = gameWorlds
                .Select(index2d => AddWorld(
                    index2d,
                    new PhysxWorld2D(_physxWorldEngineFactory.Create(index2d.Gravity, index2d.FixedDeltaTime))))
                .ToDictionary(x => x.Index.Index);
        }

        /// <summary>
        /// The tracker depends on the organizer, so it is wired after construction
        /// (see VisibilityTracker2D.WireOrganizer) instead of via the constructor.
        /// </summary>
        public void SetVisibilityTracker(IVisibilityTracker? tracker) => _visibilityTracker = tracker;

        /// <summary>Adds a new game world and initializes it.</summary>
        public virtual IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager2D(index, physx2D, _partitioner, _cache, _bodyApi, _colliderApi);
            manager.Initialize();
            _worlds[index.Index] = manager;
            return manager;
        }

        /// <summary>Removes the specified world by index.</summary>
        public virtual void RemoveWorld(int index)
        {
            _worlds.Remove(index);
        }

        /// <inheritdoc/>
        public virtual IGameWorldManager2D? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        /// <inheritdoc/>
        public virtual IGameWorldManager2D? GetWorld(string name)
        {
            return _worlds.Values.FirstOrDefault(w => w.Index.Name == name);
        }

        /// <summary>The numeric indices of all registered worlds.</summary>
        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        /// <inheritdoc/>
        public virtual IEnumerable<IGameWorldManager2D> GetAllWorlds() => _worlds.Values;

        /// <summary>Advances every world by <paramref name="deltaTime"/> seconds (the coordinator's real frame
        /// time, not a fixed step); see the class summary for the order. Runs on the engine's world thread.</summary>
        public void Step(float deltaTime)
        {
            var steppedEngines = AltruistPool.RentHashSet<object>();
            var enginesToStep = AltruistPool.RentList<IPhysxWorld2D>();
            var objectsToSync = AltruistPool.RentList<IWorldObject2D>();

            foreach (var world in _worlds.Values)
            {
                try
                {
                    foreach (var obj in world.FindAllObjects<IWorldObject2D>())
                    {
                        if (obj.Expired)
                        {
                            world.DestroyObject(obj);
                            continue;
                        }

                        objectsToSync.Add(obj);

                        try
                        {
                            obj.Step(deltaTime, world);
                        }
                        catch
                        {
                        }
                    }

                    if (world.PhysxWorld is IPhysxWorld2D physWorld2D && steppedEngines.Add(physWorld2D))
                        enginesToStep.Add(physWorld2D);
                }
                catch
                {
                }
            }

            foreach (var physWorld in enginesToStep)
            {
                try
                {
                    physWorld.Step(deltaTime);
                }
                catch
                {
                }
            }

            foreach (var obj in objectsToSync)
            {
                try
                {
                    SyncObjectFromPhysics(obj);
                }
                catch
                {
                }
            }

            // Build dimension-agnostic snapshots for shared services
            var worldSnapshots = new WorldSnapshot[_worlds.Count];
            int snapIdx = 0;
            foreach (var world in _worlds.Values)
            {
                var objs = world.GetAllObjects().Cast<ITypelessWorldObject>().ToList();
                var lookup = objs.ToDictionary(o => o.InstanceId, o => o);
                worldSnapshots[snapIdx++] = new WorldSnapshot(world.Index.Index, objs, lookup);
            }

            // AI behaviors tick (after physics, before visibility/sync)
            if (_aiBehaviorService != null)
            {
                try { _aiBehaviorService.Tick(worldSnapshots, deltaTime); }
                catch { }
            }

            // Compute visibility diffs after all positions are final
            if (_visibilityTracker is VisibilityTracker2D tracker)
            {
                try { tracker.Tick(); }
                catch { }
            }

            // Auto-sync [Synchronized] entities (delta-based, after visibility)
            if (_entitySyncService != null)
            {
                try { _entitySyncService.Tick(worldSnapshots, _engineFrequencyHz).GetAwaiter().GetResult(); }
                catch { }
            }

            AltruistPool.ReturnHashSet(steppedEngines);
            AltruistPool.ReturnList(enginesToStep);
            AltruistPool.ReturnList(objectsToSync);
        }

        private static void SyncObjectFromPhysics(IWorldObject2D obj)
        {
            if (obj.Body is not IPhysxBody2D body)
                return;

            var newPos = Position2D.Of((int)body.Position.X, (int)body.Position.Y);
            obj.Transform = obj.Transform.WithPosition(newPos);
        }

        /// <summary>True when no world is registered.</summary>
        public bool Empty() => _worlds.Count == 0;
    }
}
