/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.ThreeD;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Registry and per-frame driver of all 3D worlds (<see cref="IGameWorldManager3D"/>). Inject it to look a world
    /// up by index or name; the engine steps it automatically.
    /// </summary>
    /// <remarks>
    /// Registered only when <c>altruist:environment:mode</c> is <c>3D</c> and <c>altruist:game</c> is configured.
    /// For 2D games use <see cref="Altruist.Gaming.TwoD.IGameWorldOrganizer2D"/>. Worlds are normally created from
    /// configuration (<c>altruist:game:worlds:items</c>) at construction; <see cref="AddWorld"/> is for worlds built at runtime.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class SpawnPortal(IGameWorldOrganizer3D worlds)
    /// {
    ///     public Task Spawn(IWorldObject3D obj) =&gt; worlds.GetWorld(0)!.SpawnObject(obj);
    /// }
    /// </code>
    /// </example>
    public interface IGameWorldOrganizer3D : IGameWorldOrganizer
    {
        /// <summary>Registers a world under its <c>Index.Index</c> so it is stepped every frame.</summary>
        /// <param name="manager">The world to add.</param>
        /// <returns><paramref name="manager"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A world with the same index is already registered.</exception>
        IGameWorldManager3D AddWorld(IGameWorldManager3D manager);
        /// <summary>Unregisters the world with the given index (no-op if absent). Its objects and physics world are not disposed.</summary>
        /// <param name="index">World index (<c>IWorldIndex.Index</c>).</param>
        void RemoveWorld(int index);
        /// <summary>Returns the world with the given index, or <c>null</c>.</summary>
        /// <param name="index">World index (<c>IWorldIndex.Index</c>, from config <c>*:index</c>).</param>
        IGameWorldManager3D? GetWorld(int index);
        /// <summary>Returns the first world whose <c>Index.Name</c> equals <paramref name="name"/> (ordinal), or <c>null</c>. Linear scan; prefer <see cref="GetWorld(int)"/> on hot paths.</summary>
        /// <param name="name">World name (config <c>*:name</c>, falling back to <c>*:id</c>, then <c>"World {index}"</c>).</param>
        IGameWorldManager3D? GetWorld(string name);

        /// <summary>Enumerates all registered worlds (live view; order unspecified).</summary>
        IEnumerable<IGameWorldManager3D> GetAllWorlds();
        /// <summary>
        /// Attaches the visibility tracker ticked after each world step. Only a <see cref="VisibilityTracker3D"/> is ticked;
        /// other implementations are stored but ignored. <see cref="VisibilityTracker3D"/> calls this itself from its <c>[PostConstruct]</c> wiring.
        /// </summary>
        /// <param name="tracker">The tracker, or <c>null</c> to stop visibility ticking.</param>
        void SetVisibilityTracker(IVisibilityTracker? tracker);
    }

    // Stepped by the WorldCoordinator (the engine's IGameWorldOrganizer) with the variable frame time.
    /// <summary>
    /// Default <see cref="IGameWorldOrganizer3D"/>, also an <see cref="IWorldStepper"/> in variable-step mode. Each
    /// <see cref="Step"/>: for every world (in parallel when there is more than one) destroys expired objects, calls
    /// each object's <c>Step(dt, world)</c>, steps physics and copies body position/rotation back to <c>Transform</c>
    /// (or via <see cref="IPhysicsTransformSync3D"/>); then records position history, ticks AI, visibility (on a
    /// background task) and entity sync with the shared per-world <see cref="WorldSnapshot"/>s.
    /// </summary>
    /// <remarks>
    /// Singleton DI service. Config: <c>altruist:game:worlds:entity-sync-hz</c> (default 25). Exceptions thrown by
    /// object <c>Step</c>, physics, AI and entity sync are swallowed silently; only visibility errors are logged to stderr.
    /// Because worlds step in parallel, object <c>Step</c> code must not touch other worlds.
    /// </remarks>
    [Service(typeof(IWorldStepper))]
    [Service(typeof(IGameWorldOrganizer3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game")]
    public class GameWorldOrganizer3D : IGameWorldOrganizer3D, IWorldStepper
    {
        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldLoader3D _worldLoader;
        private readonly IEntitySyncService? _entitySyncService;
        private readonly IAIBehaviorService? _aiBehaviorService;
        private readonly IPositionHistoryRecorder? _positionRecorder;
        private IVisibilityTracker? _visibilityTracker;
        private readonly ILogger _logger;
        /// <summary>
        /// Rate the entity sync throttling ([Synchronized(Frequency)]) assumes for the world step:
        /// <c>altruist:game:worlds:entity-sync-hz</c> (default 25).
        /// </summary>
        private readonly float _engineFrequencyHz;

        /// <summary>Fallback entity sync rate (Hz) when the configured value is missing or not positive.</summary>
        public const float DefaultEntitySyncHz = 25f;

        /// <summary>The sync rate passed to the entity sync service (<c>altruist:game:worlds:entity-sync-hz</c>).</summary>
        public float EntitySyncHz => _engineFrequencyHz;
        private long _stepCount;

        /// <summary>
        /// Creates the organizer and synchronously loads every configured world through <paramref name="worldLoader"/>
        /// (blocking on <see cref="IWorldLoader3D.LoadFromIndex"/>).
        /// </summary>
        /// <param name="worldLoader">Loader that builds a manager per world index.</param>
        /// <param name="gameWorlds">All configured world indices.</param>
        /// <param name="entitySyncService">Optional entity sync service ticked each step.</param>
        /// <param name="aiBehaviorService">Optional AI service ticked each step.</param>
        /// <param name="positionRecorder">Optional lag-compensation position recorder.</param>
        /// <param name="entitySyncHz">Rate passed to the entity sync service (<c>altruist:game:worlds:entity-sync-hz</c>).</param>
        /// <param name="loggerFactory">Receives the failures of individual objects and services during a step; null discards them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="gameWorlds"/> is <c>null</c>.</exception>
        public GameWorldOrganizer3D(
            IWorldLoader3D worldLoader,
            IEnumerable<IWorldIndex3D> gameWorlds,
            IEntitySyncService? entitySyncService = null,
            IAIBehaviorService? aiBehaviorService = null,
            IPositionHistoryRecorder? positionRecorder = null,
            [AppConfigValue("altruist:game:worlds:entity-sync-hz", "25")] float entitySyncHz = DefaultEntitySyncHz,
            ILoggerFactory? loggerFactory = null
        )
        {
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<GameWorldOrganizer3D>();
            _worldLoader = worldLoader;
            _entitySyncService = entitySyncService;
            _aiBehaviorService = aiBehaviorService;
            _positionRecorder = positionRecorder;
            _engineFrequencyHz = entitySyncHz > 0 ? entitySyncHz : DefaultEntitySyncHz;

            if (gameWorlds is null)
                throw new ArgumentNullException(nameof(gameWorlds));

            // Initialize worlds synchronously — LoadFromIndex returns immediately
            // for worlds without a DataPath (creates empty physics world).
            foreach (var index in gameWorlds)
            {
                var manager = _worldLoader.LoadFromIndex(index).GetAwaiter().GetResult();
                AddWorld(manager);
            }
        }

        /// <inheritdoc/>
        public void SetVisibilityTracker(IVisibilityTracker? tracker)
        {
            _visibilityTracker = tracker;
        }

        /// <summary>Flat enumeration of every 3D entity across all worlds.
        /// Used by <see cref="IPositionHistoryRecorder"/> so the recorder
        /// doesn't need to pull the organizer via DI (one-way push avoids a cycle).</summary>
        private static IEnumerable<IWorldObject3D> EnumerateAllEntities(IGameWorldManager3D[] worlds)
        {
            for (int i = 0; i < worlds.Length; i++)
            {
                foreach (var obj in worlds[i].FindAllObjects<IWorldObject3D>())
                    yield return obj;
            }
        }

        private async Task InitializeWorlds(IEnumerable<IWorldIndex3D> worlds)
        {
            foreach (var index in worlds)
            {
                var manager = await _worldLoader.LoadFromIndex(index);
                AddWorld(manager);
            }
        }

        /// <inheritdoc/>
        public IGameWorldManager3D AddWorld(IGameWorldManager3D manager)
        {
            if (manager is null)
                throw new ArgumentNullException(nameof(manager));

            var idx = manager.Index.Index;
            if (_worlds.ContainsKey(idx))
                throw new InvalidOperationException($"World {idx} already exists.");

            _worlds[idx] = manager;
            return manager;
        }

        /// <inheritdoc/>
        public virtual void RemoveWorld(int index)
        {
            _worlds.Remove(index);
        }

        /// <inheritdoc/>
        public virtual IGameWorldManager3D? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        /// <inheritdoc/>
        public virtual IGameWorldManager3D? GetWorld(string name)
        {
            return _worlds
                .Where(x => x.Value.Index.Name == name)
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        /// <summary>Indices of all registered worlds.</summary>
        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        /// <summary>Advances every world by <paramref name="deltaTime"/> and runs AI, visibility and entity sync; called by the world coordinator once per frame.</summary>
        /// <param name="deltaTime">Elapsed real time in seconds.</param>
        /// <remarks>A failing object, physics step or service is logged and the rest of the step still runs; an exception
        /// outside those (building the snapshots) is logged and rethrown to the coordinator.</remarks>
        public void Step(float deltaTime)
        {
            _stepCount++;
            try
            {
                var worlds = _worlds.Values.ToArray();

                if (worlds.Length <= 1)
                {
                    foreach (var world in worlds)
                        StepWorld(world, deltaTime);
                }
                else
                {
                    Parallel.ForEach(worlds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                        world => StepWorld(world, deltaTime));
                }

                if (_positionRecorder != null)
                    _positionRecorder.RecordSnapshot(Altruist.Engine.AltruistEngine.CurrentTick, EnumerateAllEntities(worlds));

                var worldSnapshots = new WorldSnapshot[worlds.Length];
                for (int i = 0; i < worlds.Length; i++)
                {
                    var (list, lookup) = worlds[i].GetCachedSnapshot();
                    var typelessList = (IReadOnlyList<ITypelessWorldObject>)list;
                    // Dictionary is invariant on TValue — cannot cast directly.
                    // Wrap with a covariant read-only view.
                    var typelessLookup = new Dictionary<string, ITypelessWorldObject>(lookup.Count);
                    foreach (var kvp in lookup)
                        typelessLookup[kvp.Key] = kvp.Value;
                    worldSnapshots[i] = new WorldSnapshot(worlds[i].Index.Index, typelessList, typelessLookup);
                }

                if (_aiBehaviorService != null)
                {
                    try { _aiBehaviorService.Tick(worldSnapshots, deltaTime); }
                    catch (Exception ex) { _logger.LogError(ex, "AI tick failed at step {Step}.", _stepCount); }
                }

                var visTask = Task.CompletedTask;
                if (_visibilityTracker is VisibilityTracker3D tracker)
                {
                    visTask = Task.Run(() =>
                    {
                        try { tracker.Tick(worldSnapshots); }
                        catch (Exception ex) { _logger.LogError(ex, "Visibility tick failed at step {Step}.", _stepCount); }
                    });
                }

                if (_entitySyncService != null)
                {
                    try { _entitySyncService.Tick(worldSnapshots, _engineFrequencyHz).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _logger.LogError(ex, "Entity sync failed at step {Step}.", _stepCount); }
                }

                visTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "World step {Step} failed.", _stepCount);
                throw;
            }
        }

        private void StepWorld(IGameWorldManager3D world, float deltaTime)
        {
            var objectsToSync = AltruistPool.RentList<IWorldObject3D>();

            foreach (var obj in world.FindAllObjects<IWorldObject3D>())
            {
                if (obj.Expired)
                {
                    world.DestroyObject(obj);
                    continue;
                }

                try { obj.Step(deltaTime, world); }
                catch (Exception ex) { _logger.LogError(ex, "Step of {Object} in world {World} failed.", obj.InstanceId, world.Index.Index); }

                objectsToSync.Add(obj);
            }

            var physWorld = world.PhysxWorld;
            if (physWorld?.Engine != null)
            {
                try { physWorld.Step(deltaTime); }
                catch (Exception ex) { _logger.LogError(ex, "Physics step of world {World} failed.", world.Index.Index); }
            }

            foreach (var obj in objectsToSync)
            {
                try { SyncObjectFromPhysics(obj); }
                catch (Exception ex) { _logger.LogError(ex, "Physics sync of {Object} in world {World} failed.", obj.InstanceId, world.Index.Index); }
            }

            AltruistPool.ReturnList(objectsToSync);
        }

        private static void SyncObjectFromPhysics(IWorldObject3D obj)
        {
            if (obj.Body is not IPhysxBody3D body)
                return;

            var bodyTransform = obj.Transform
                .WithPosition(Position3D.From(body.Position))
                .WithRotation(Rotation3D.FromQuaternion(body.Rotation));

            obj.Transform = obj is IPhysicsTransformSync3D transformSync
                ? transformSync.GetWorldTransformFromPhysics(body)
                : bodyTransform;

            if (obj.Colliders != null)
            {
                foreach (var col in obj.Colliders)
                    col.Transform = bodyTransform;
            }
        }

        /// <summary><c>true</c> when no world is registered.</summary>
        public bool Empty() => _worlds.Count == 0;

        /// <inheritdoc/>
        public IEnumerable<IGameWorldManager3D> GetAllWorlds()
        {
            return _worlds.Values;
        }
    }
}
