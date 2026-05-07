/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.ThreeD
{
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

        public float ViewRange { get; set; } = 5000f;

        public event Action<VisibilityChange>? OnEntityVisible;
        public event Action<VisibilityChange>? OnEntityInvisible;

        public VisibilityTracker3D(
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f,
            IEntityHibernationService? hibernation = null,
            ISpatialCollisionDispatcher? collisionDispatcher = null)
        {
            ViewRange = viewRange;
            _hibernation = hibernation;
            _collisionDispatcher = collisionDispatcher;
        }

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

                        // Stagger: only process half the observers per tick (8+ observers)
                        if (shouldStagger && staggerGroup != (_tickCounter & 1))
                            return;

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

        public bool Observe(ITypelessWorldObject observer)
        {
            if (observer is not IWorldObject3D worldObject)
                return false;

            if (string.IsNullOrEmpty(worldObject.ClientId))
                return false;

            _observers[worldObject.ClientId] = worldObject;
            _observerInstanceIds[worldObject.InstanceId] = worldObject.ClientId;
            RefreshObserver(worldObject.ClientId);
            return true;
        }

        public IReadOnlySet<string>? GetVisibleEntities(string clientId)
        {
            return _visibleSets.TryGetValue(clientId, out var set) ? set : null;
        }

        public IEnumerable<string> GetObserversOf(string entityInstanceId)
        {
            foreach (var (clientId, visibleSet) in _visibleSets)
            {
                if (visibleSet.Contains(entityInstanceId))
                    yield return clientId;
            }
        }

        public IEnumerable<ITypelessWorldObject> GetObservers()
        {
            foreach (var observer in _observers.Values.ToArray())
                yield return observer;
        }

        public void RefreshObserver(string clientId)
        {
            _visibleSets.TryRemove(clientId, out _);
        }

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

        public void RemoveObserver(string clientId)
        {
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
