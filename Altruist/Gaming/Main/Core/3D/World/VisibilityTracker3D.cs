/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Default 3D <see cref="IVisibilityTracker"/>: each world step it computes, for every registered observer, the set
    /// of world objects within <see cref="ViewRange"/> (3D sphere on <c>Transform.Position</c>) and raises
    /// <see cref="OnEntityVisible"/> / <see cref="OnEntityInvisible"/> for the difference to the previous tick.
    /// Spawns and destroys are also reported immediately via the world's <c>OnObjectCreated</c>/<c>OnObjectDestroyed</c> events.
    /// </summary>
    /// <remarks>
    /// <para>Singleton when <c>altruist:environment:mode</c> is <c>3D</c> and <c>altruist:game</c> is set; ticked by
    /// <see cref="GameWorldOrganizer3D"/> on a background task. Config: <c>altruist:game:visibility:range</c> (default 5000 world units).
    /// For 2D use <see cref="Altruist.Gaming.TwoD.VisibilityTracker2D"/>.</para>
    /// <para>Observers must be registered explicitly with <see cref="Observe"/> and need a non-empty <c>ClientId</c>.
    /// With 4+ observers the work runs in <see cref="Parallel"/> and events fire on thread-pool threads, so subscribers
    /// must be thread-safe; with 8+ observers only half of them (alternating groups) are refreshed per tick (an observer
    /// not refreshed keeps its previous visible set, and those entities count as observed for hibernation).
    /// Above 200 objects a spatial hash grid (cell size max(ViewRange/2, 500)) is used as broadphase.</para>
    /// <para>When an <see cref="IEntityHibernationService"/> is registered, hibernated entities near observers are woken
    /// (re-spawned as dynamic objects) and <see cref="IHibernatable"/> objects seen by no observer are destroyed and hibernated.
    /// When an <see cref="ISpatialCollisionDispatcher"/> is registered, <c>EntityVisible</c>/<c>EntityInvisible</c>
    /// collision events are dispatched for each change.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// tracker.OnEntityVisible += c =&gt; SendSpawn(c.ObserverClientId, c.Target);
    /// tracker.OnEntityInvisible += c =&gt; SendDespawn(c.ObserverClientId, c.Target);
    /// tracker.Observe(playerObject); // playerObject.ClientId must be set
    /// </code>
    /// </example>
    [Service(typeof(IVisibilityTracker))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game")]
    public class VisibilityTracker3D : IVisibilityTracker
    {
        private IGameWorldOrganizer3D? _organizer;
        private readonly IEntityHibernationService? _hibernation;
        private readonly ISpatialCollisionDispatcher? _collisionDispatcher;
        private readonly ConcurrentDictionary<string, HashSet<string>> _visibleSets = new();
        private readonly ConcurrentDictionary<string, IWorldObject3D> _observers = new();
        private readonly ConcurrentDictionary<string, string> _observerInstanceIds = new();

        // Tracks how many observers see each non-player entity
        private readonly ConcurrentDictionary<string, int> _observerCounts = new();

        // Spatial broadphase grid for large worlds
        private SpatialHashGrid? _grid;
        private readonly List<int> _gridQueryBuffer = new(256);
        private bool _useSpatialGrid;

        // Staggered ticking: process half the observers per tick (alternating groups)
        private uint _tickCounter;

        // Per-thread scratch buffers for parallel observer processing
        private readonly ConcurrentDictionary<int, (HashSet<string> visible, List<int> gridBuf, List<string> removeBuf)> _threadBuffers = new();

        /// <inheritdoc/>
        public float ViewRange { get; set; } = 5000f;

        /// <inheritdoc/>
        public event Action<VisibilityChange>? OnEntityVisible;
        /// <inheritdoc/>
        public event Action<VisibilityChange>? OnEntityInvisible;

        private readonly ILogger? _logger;

        /// <summary>Creates the tracker; the organizer is wired later by <see cref="WireOrganizer"/>.</summary>
        /// <param name="viewRange">Visibility radius in world units (<c>altruist:game:visibility:range</c>, default 5000).</param>
        /// <param name="hibernation">Optional hibernation service; enables wake/hibernate phases.</param>
        /// <param name="collisionDispatcher">Optional dispatcher for visibility collision events.</param>
        /// <param name="loggerFactory">Optional logger factory.</param>
        public VisibilityTracker3D(
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f,
            IEntityHibernationService? hibernation = null,
            ISpatialCollisionDispatcher? collisionDispatcher = null,
            ILoggerFactory? loggerFactory = null)
        {
            ViewRange = viewRange;
            _hibernation = hibernation;
            _collisionDispatcher = collisionDispatcher;
            _logger = loggerFactory?.CreateLogger<VisibilityTracker3D>();
        }

        /// <summary>
        /// Sets the organizer used to resolve worlds and subscribes to spawn/destroy events of the worlds registered at this time
        /// (worlds added later are not subscribed). Normally called by <see cref="WireOrganizer"/>; calling it twice subscribes twice.
        /// </summary>
        /// <param name="organizer">The 3D world organizer.</param>
        public void SetOrganizer(IGameWorldOrganizer3D organizer)
        {
            _organizer = organizer;

            // Subscribe to spawn/destroy events on every world so we can synchronously
            // notify in-range observers — no dependence on snapshot rebuild timing.
            foreach (var world in organizer.GetAllWorlds())
            {
                int worldIndex = world.Index.Index;
                world.OnObjectCreated += obj => HandleObjectCreated(obj, worldIndex);
                world.OnObjectDestroyed += obj => HandleObjectDestroyed(obj, worldIndex);
            }
        }

        /// <summary>
        /// Wires the circular tracker ↔ organizer dependency at the point both
        /// instances are guaranteed to be the bootstrap provider's. EngineStartupConfiguration
        /// also performs this wiring during its <c>Configure</c> phase, but that runs against
        /// a temp provider whose instances get garbage-collected; with per-provider singleton
        /// lifetime the bootstrap provider builds its own tracker and organizer that would
        /// otherwise stay un-wired — Tick() returns early on null _organizer and visibility
        /// broadcasts (SCharacterAdd / SCharacterRemove) never fire.
        /// </summary>
        [PostConstruct]
        public void WireOrganizer(IGameWorldOrganizer3D organizer)
        {
            if (_organizer is not null) return;
            organizer.SetVisibilityTracker(this);
            SetOrganizer(organizer);
        }

        private void HandleObjectCreated(IWorldObject3D obj, int worldIndex)
        {
            var instanceId = obj.InstanceId;
            var objPos = obj.Transform.Position;
            float rangeSq = ViewRange * ViewRange;

            // Broadcast to every known observer in range right now. Observers outside
            // range will still be picked up by the next Tick when they move closer.
            foreach (var (observerClientId, observer) in _observers)
            {
                if (observer.InstanceId == instanceId) continue;

                var op = observer.Transform.Position;
                if (DistanceSq(op, objPos) > rangeSq) continue;

                var visibleSet = _visibleSets.GetOrAdd(observerClientId, static _ => new HashSet<string>());
                if (!visibleSet.Add(instanceId)) continue;

                OnEntityVisible?.Invoke(new VisibilityChange
                {
                    ObserverClientId = observerClientId,
                    Target = obj,
                    WorldIndex = worldIndex,
                });

                _collisionDispatcher?.Dispatch(observer, obj, typeof(Physx.EntityVisible));
            }
        }

        private void HandleObjectDestroyed(IWorldObject3D obj, int worldIndex)
        {
            var instanceId = obj.InstanceId;

            foreach (var (observerClientId, visibleSet) in _visibleSets)
            {
                if (!visibleSet.Remove(instanceId)) continue;

                OnEntityInvisible?.Invoke(new VisibilityChange
                {
                    ObserverClientId = observerClientId,
                    Target = obj,
                    WorldIndex = worldIndex,
                });

                if (_observers.TryGetValue(observerClientId, out var observer))
                    _collisionDispatcher?.Dispatch(observer, obj, typeof(Physx.EntityInvisible));
            }
        }

        /// <summary>
        /// Recomputes visibility for all observers in each world snapshot and raises enter/leave events. Called by
        /// <see cref="GameWorldOrganizer3D"/> after each world step; no-op until an organizer is wired.
        /// </summary>
        /// <param name="snapshots">Per-world object snapshots for this tick.</param>
        public void Tick(WorldSnapshot[] snapshots)
        {
            if (_organizer is null) return;
            _tickCounter++;

            foreach (var snapshot in snapshots)
            {
                var worldIndex = snapshot.WorldIndex;
                var world = _organizer!.GetWorld(worldIndex);
                if (world is null) continue;
                var allObjects = snapshot.AllObjects;
                var lookup = snapshot.Lookup;

                // Reset observer counts for this tick
                _observerCounts.Clear();

                // Build spatial broadphase grid (O(n), reused by all observers)
                _grid ??= new SpatialHashGrid(cellSize: MathF.Max(ViewRange * 0.5f, 500f));
                _grid.Build(allObjects);
                _useSpatialGrid = allObjects.Count > 200;

                // Phase 1: Wake hibernated entities near any observer
                if (_hibernation != null)
                    WakeNearbyHibernated(world, allObjects);

                // Phase 2: Collect observers
                var observerList = CollectObservers(lookup);

                // Phase 3: Compute visibility — parallel when enough observers
                bool shouldStagger = observerList.Count >= 8; // Only stagger with many observers

                if (observerList.Count >= 4)
                {
                    // Parallel per-observer visibility computation
                    Parallel.For(0, observerList.Count, i =>
                    {
                        var (obs, staggerGroup) = observerList[i];

                        // Stagger: only process half the observers per tick (8+ observers). A skipped
                        // observer still sees what it saw last tick, which keeps those entities awake.
                        if (shouldStagger && staggerGroup != (_tickCounter & 1))
                        {
                            CountStillVisible(obs.ClientId);
                            return;
                        }

                        UpdateVisibilityForParallel(obs, world, worldIndex, allObjects, lookup);
                    });
                }
                else
                {
                    // Sequential for small observer counts (no staggering, no thread overhead)
                    for (int i = 0; i < observerList.Count; i++)
                    {
                        var (obs, _) = observerList[i];
                        UpdateVisibilityFor(obs, worldIndex, allObjects, lookup);
                    }
                }

                // Phase 4: Hibernate entities with zero observers
                if (_hibernation != null)
                    HibernateUnobserved(world, allObjects);
            }
        }

        private void CountStillVisible(string clientId)
        {
            if (!_visibleSets.TryGetValue(clientId, out var visible)) return;
            foreach (var id in visible)
                _observerCounts.AddOrUpdate(id, 1, (_, c) => c + 1);
        }

        // Reusable observer list — avoids allocation
        private readonly List<(IWorldObject3D observer, uint staggerGroup)> _observerCollectBuffer = new();

        private List<(IWorldObject3D observer, uint staggerGroup)> CollectObservers(
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup)
        {
            _observerCollectBuffer.Clear();
            uint group = 0;

            foreach (var (clientId, registeredObserver) in _observers.ToArray())
            {
                if (!lookup.TryGetValue(registeredObserver.InstanceId, out var current) ||
                    current is not IWorldObject3D observer ||
                    string.IsNullOrEmpty(observer.ClientId))
                {
                    continue;
                }

                if (!string.Equals(observer.ClientId, clientId, StringComparison.Ordinal))
                {
                    RemoveObserver(clientId);
                    Observe(observer);
                    continue;
                }

                _observers[clientId] = observer;
                _observerInstanceIds[observer.InstanceId] = clientId;
                _observerCollectBuffer.Add((observer, group & 1));
                group++;
            }

            return _observerCollectBuffer;
        }

        private readonly HashSet<string> _wokenBuffer = new();

        private void WakeNearbyHibernated(IGameWorldManager3D world, IReadOnlyList<ITypelessWorldObject> allObjects)
        {
            if (_hibernation == null || _hibernation.Count == 0) return;

            _wokenBuffer.Clear();

            foreach (var observer in _observers.Values)
            {
                var pos = observer.Transform.Position;
                var nearby = _hibernation.FindNearby(pos.X, pos.Y, pos.Z, ViewRange);

                foreach (var hibernated in nearby)
                {
                    if (_wokenBuffer.Contains(hibernated.InstanceId)) continue;

                    var entry = _hibernation.Wake(hibernated.InstanceId);
                    if (entry?.Entity is IWorldObject3D worldObj)
                    {
                        try
                        {
                            world.SpawnDynamicObject(worldObj).GetAwaiter().GetResult();
                            _wokenBuffer.Add(hibernated.InstanceId);
                        }
                        catch { /* entity may already exist */ }
                    }
                }
            }
        }

        private void HibernateUnobserved(IGameWorldManager3D world, IReadOnlyList<ITypelessWorldObject> allObjects)
        {
            if (_hibernation == null) return;

            for (int i = 0; i < allObjects.Count; i++)
            {
                if (allObjects[i] is not IWorldObject3D obj) continue;
                if (_observerInstanceIds.ContainsKey(obj.InstanceId)) continue;
                if (obj is not IHibernatable hibernatable) continue;
                if (hibernatable.IsHibernated || !hibernatable.CanHibernate) continue;

                _observerCounts.TryGetValue(obj.InstanceId, out var count);
                if (count == 0)
                {
                    var pos = obj.Transform.Position;
                    var vnum = 0;
                    if (obj is IVnumProvider vnumProvider)
                        vnum = vnumProvider.Vnum;

                    // If this entity is currently in any observer's visible set, hibernating
                    // it now will trigger HandleObjectDestroyed → fires Invisible → client gets
                    // SCharacterRemove. Then next tick WakeNearbyHibernated re-spawns it →
                    // fires Visible → client gets SCharacterAdd. That's the per-entity flicker
                    // we observed (vid X with 37+ ADD events suppressed in seconds).
                    bool wasVisibleToSomeone = false;
                    foreach (var (_, set) in _visibleSets)
                    {
                        if (set.Contains(obj.InstanceId)) { wasVisibleToSomeone = true; break; }
                    }
                    if (wasVisibleToSomeone)
                    {
                        _logger?.LogWarning(
                            "[VIS-HIBERNATE-VISIBLE] entity instance={InstanceId} vnum={Vnum} pos=({X:F1},{Y:F1},{Z:F1}) " +
                            "was in some observer's visible set when hibernated — will trigger spurious SCharacterRemove → SCharacterAdd flicker on the client",
                            obj.InstanceId, vnum, pos.X, pos.Y, pos.Z);
                    }

                    var zoneName = obj.ZoneId ?? "";
                    world.DestroyObject(obj);
                    _hibernation.Hibernate(obj.InstanceId, zoneName, pos.X, pos.Y, pos.Z, vnum, hibernatable);
                }
            }
        }

        // Single-threaded scratch collections (used when observer count < 4)
        private readonly Dictionary<string, HashSet<string>> _scratchVisible = new();
        private readonly List<string> _removeBuffer = new();

        /// <summary>Sequential path — uses shared scratch buffers.</summary>
        private void UpdateVisibilityFor(
            IWorldObject3D observer, int worldIndex,
            IReadOnlyList<ITypelessWorldObject> allObjects,
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup)
        {
            var clientId = observer.ClientId;
            var pos = observer.Transform.Position;

            if (!_scratchVisible.TryGetValue(clientId, out var currentlyVisible))
            {
                currentlyVisible = new HashSet<string>();
                _scratchVisible[clientId] = currentlyVisible;
            }
            else
                currentlyVisible.Clear();

            float rangeSq = ViewRange * ViewRange;
            ComputeVisible(observer, pos, rangeSq, allObjects, currentlyVisible);
            ApplyVisibilityDiff(clientId, observer, worldIndex, currentlyVisible, lookup, _removeBuffer);
        }

        /// <summary>Parallel path — uses per-thread scratch buffers.</summary>
        private void UpdateVisibilityForParallel(
            IWorldObject3D observer, IGameWorldManager3D world, int worldIndex,
            IReadOnlyList<ITypelessWorldObject> allObjects,
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup)
        {
            var threadId = Environment.CurrentManagedThreadId;
            var (currentlyVisible, gridBuf, removeBuf) = _threadBuffers.GetOrAdd(threadId,
                _ => (new HashSet<string>(), new List<int>(256), new List<string>(64)));

            currentlyVisible.Clear();

            var clientId = observer.ClientId;
            var pos = observer.Transform.Position;
            float rangeSq = ViewRange * ViewRange;

            // Use per-thread grid buffer for spatial query
            if (_useSpatialGrid && _grid != null)
            {
                _grid.QueryRadius(pos.X, pos.Y, pos.Z, ViewRange, gridBuf);
                for (int q = 0; q < gridBuf.Count; q++)
                {
                    var idx = gridBuf[q];
                    if (allObjects[idx] is not IWorldObject3D target) continue;
                    if (target.InstanceId == observer.InstanceId) continue;

                    var tp = target.Transform.Position;
                    if (DistanceSq(tp, pos) <= rangeSq)
                    {
                        currentlyVisible.Add(target.InstanceId);
                        _observerCounts.AddOrUpdate(target.InstanceId, 1, (_, c) => c + 1);
                    }
                }
            }
            else
            {
                for (int i = 0; i < allObjects.Count; i++)
                {
                    if (allObjects[i] is not IWorldObject3D target) continue;
                    if (target.InstanceId == observer.InstanceId) continue;

                    var tp = target.Transform.Position;
                    if (DistanceSq(tp, pos) <= rangeSq)
                    {
                        currentlyVisible.Add(target.InstanceId);
                        _observerCounts.AddOrUpdate(target.InstanceId, 1, (_, c) => c + 1);
                    }
                }
            }

            // Apply diff (events fire on thread pool — subscribers must be thread-safe)
            ApplyVisibilityDiff(clientId, observer, worldIndex, currentlyVisible, lookup, removeBuf);
        }

        private void ComputeVisible(
            IWorldObject3D observer, Altruist.ThreeD.Numerics.Position3D pos, float rangeSq,
            IReadOnlyList<ITypelessWorldObject> allObjects, HashSet<string> currentlyVisible)
        {
            if (_useSpatialGrid && _grid != null)
            {
                _grid.QueryRadius(pos.X, pos.Y, pos.Z, ViewRange, _gridQueryBuffer);
                for (int q = 0; q < _gridQueryBuffer.Count; q++)
                {
                    var idx = _gridQueryBuffer[q];
                    if (allObjects[idx] is not IWorldObject3D target) continue;
                    if (target.InstanceId == observer.InstanceId) continue;

                    var tp = target.Transform.Position;
                    if (DistanceSq(tp, pos) <= rangeSq)
                    {
                        currentlyVisible.Add(target.InstanceId);
                        if (_observerCounts.TryGetValue(target.InstanceId, out var c))
                            _observerCounts[target.InstanceId] = c + 1;
                        else
                            _observerCounts[target.InstanceId] = 1;
                    }
                }
            }
            else
            {
                for (int i = 0; i < allObjects.Count; i++)
                {
                    if (allObjects[i] is not IWorldObject3D target) continue;
                    if (target.InstanceId == observer.InstanceId) continue;

                    var tp = target.Transform.Position;
                    if (DistanceSq(tp, pos) <= rangeSq)
                    {
                        currentlyVisible.Add(target.InstanceId);
                        if (_observerCounts.TryGetValue(target.InstanceId, out var c))
                            _observerCounts[target.InstanceId] = c + 1;
                        else
                            _observerCounts[target.InstanceId] = 1;
                    }
                }
            }
        }

        private void ApplyVisibilityDiff(
            string clientId, IWorldObject3D observer, int worldIndex,
            HashSet<string> currentlyVisible,
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup,
            List<string> removeBuf)
        {
            var previouslyVisible = _visibleSets.GetOrAdd(clientId, static _ => new HashSet<string>());

            // Entities that just became visible
            foreach (var instanceId in currentlyVisible)
            {
                if (previouslyVisible.Add(instanceId))
                {
                    if (lookup.TryGetValue(instanceId, out var target))
                    {
                        OnEntityVisible?.Invoke(new VisibilityChange
                        {
                            ObserverClientId = clientId,
                            Target = target,
                            WorldIndex = worldIndex,
                        });

                        _collisionDispatcher?.Dispatch(observer, target, typeof(Physx.EntityVisible));
                    }
                }
            }

            // Entities that just became invisible
            removeBuf.Clear();
            foreach (var instanceId in previouslyVisible)
            {
                if (!currentlyVisible.Contains(instanceId))
                {
                    if (lookup.TryGetValue(instanceId, out var target))
                    {
                        if (target is IWorldObject3D target3D)
                        {
                            var observerPos = observer.Transform.Position;
                            var targetPos = target3D.Transform.Position;
                            float liveRangeSq = ViewRange * ViewRange;

                            // Guard against transient false negatives from snapshot/grid churn:
                            // if the live positions still say the entity is in range, keep it visible.
                            if (DistanceSq(targetPos, observerPos) <= liveRangeSq)
                                continue;
                        }

                        removeBuf.Add(instanceId);
                        OnEntityInvisible?.Invoke(new VisibilityChange
                        {
                            ObserverClientId = clientId,
                            Target = target,
                            WorldIndex = worldIndex,
                        });

                        _collisionDispatcher?.Dispatch(observer, target, typeof(Physx.EntityInvisible));
                    }
                    else
                    {
                        removeBuf.Add(instanceId);
                    }
                }
            }

            for (int i = 0; i < removeBuf.Count; i++)
                previouslyVisible.Remove(removeBuf[i]);
        }

        /// <inheritdoc/>
        /// <returns><c>false</c> when <paramref name="observer"/> is not an <see cref="IWorldObject3D"/> or has an empty <c>ClientId</c>.</returns>
        public bool Observe(ITypelessWorldObject observer)
        {
            if (observer is not IWorldObject3D worldObject)
                return false;

            if (string.IsNullOrEmpty(worldObject.ClientId))
                return false;

            _observers[worldObject.ClientId] = worldObject;
            _observerInstanceIds[worldObject.InstanceId] = worldObject.ClientId;
            RefreshObserver(worldObject.ClientId);
            _logger?.LogInformation("[VIS-OBS-ADD] {ClientId} observer registered (instance={InstanceId}) — visible-set will rebuild from scratch this tick",
                worldObject.ClientId, worldObject.InstanceId);
            return true;
        }

        /// <inheritdoc/>
        public IReadOnlySet<string>? GetVisibleEntities(string clientId)
        {
            return _visibleSets.TryGetValue(clientId, out var set) ? set : null;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetObserversOf(string entityInstanceId)
        {
            foreach (var (clientId, visibleSet) in _visibleSets)
            {
                if (visibleSet.Contains(entityInstanceId))
                    yield return clientId;
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ITypelessWorldObject> GetObservers()
        {
            foreach (var observer in _observers.Values.ToArray())
                yield return observer;
        }

        /// <summary>
        /// Clears the observer's visible set so the next tick re-raises <see cref="OnEntityVisible"/> for everything in range
        /// (no <see cref="OnEntityInvisible"/> is raised). Use after a teleport or when the client needs a full resend.
        /// </summary>
        /// <param name="clientId">Observer client id.</param>
        public void RefreshObserver(string clientId)
        {
            _visibleSets.TryRemove(clientId, out _);
        }

        /// <inheritdoc/>
        public void RemoveObserver(ITypelessWorldObject observer)
        {
            if (observer is not IWorldObject3D worldObject)
                return;

            if (_observerInstanceIds.TryGetValue(worldObject.InstanceId, out var clientId))
            {
                RemoveObserver(clientId);
                return;
            }

            if (!string.IsNullOrEmpty(worldObject.ClientId))
                RemoveObserver(worldObject.ClientId);
        }

        /// <inheritdoc/>
        public void RemoveObserver(string clientId)
        {
            int visibleCount = _visibleSets.TryGetValue(clientId, out var existingSet) ? existingSet.Count : 0;
            _logger?.LogWarning("[VIS-OBS-REMOVE] {ClientId} observer removed — will fire Invisible for {Count} entities (this is the 'all mobs disappeared' signature if not from a real disconnect)",
                clientId, visibleCount);
            if (_visibleSets.TryRemove(clientId, out var visible) && visible.Count > 0)
            {
                if (_organizer is null)
                {
                    if (_observers.TryRemove(clientId, out var removedObserver))
                        _observerInstanceIds.TryRemove(removedObserver.InstanceId, out _);
                    return;
                }
                foreach (var world in _organizer.GetAllWorlds())
                {
                    var (_, lookup) = world.GetCachedSnapshot();
                    foreach (var instanceId in visible)
                    {
                        if (lookup.TryGetValue(instanceId, out var target))
                        {
                            OnEntityInvisible?.Invoke(new VisibilityChange
                            {
                                ObserverClientId = clientId,
                                Target = target,
                                WorldIndex = world.Index.Index,
                            });

                            if (_observers.TryGetValue(clientId, out var observer))
                                _collisionDispatcher?.Dispatch(observer, target, typeof(Physx.EntityInvisible));
                        }
                    }
                }
            }

            if (_observers.TryRemove(clientId, out var observerToRemove))
                _observerInstanceIds.TryRemove(observerToRemove.InstanceId, out _);
        }

        private static float DistanceSq(
            Altruist.ThreeD.Numerics.Position3D a,
            Altruist.ThreeD.Numerics.Position3D b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
