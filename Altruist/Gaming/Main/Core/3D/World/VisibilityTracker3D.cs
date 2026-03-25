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

        // Tracks how many observers see each non-player entity
        private readonly ConcurrentDictionary<string, int> _observerCounts = new();

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

        public void SetOrganizer(IGameWorldOrganizer3D organizer) => _organizer = organizer;

        public void Tick(WorldSnapshot[] snapshots)
        {
            if (_organizer is null) return;

            foreach (var snapshot in snapshots)
            {
                var worldIndex = snapshot.WorldIndex;
                var world = _organizer!.GetWorld(worldIndex);
                if (world is null) continue;
                var allObjects = snapshot.AllObjects;
                var lookup = snapshot.Lookup;

                // Reset observer counts for this tick
                _observerCounts.Clear();

                // Phase 1: Wake hibernated entities near any observer
                if (_hibernation != null)
                {
                    WakeNearbyHibernated(world, allObjects);
                }

                // Phase 2: Compute visibility for each observer (player)
                for (int i = 0; i < allObjects.Count; i++)
                {
                    if (allObjects[i] is not IWorldObject3D obj3d) continue;
                    if (string.IsNullOrEmpty(obj3d.ClientId))
                        continue;

                    _observers[obj3d.ClientId] = obj3d;
                    UpdateVisibilityFor(obj3d, world, worldIndex, allObjects, lookup);
                }

                // Phase 3: Hibernate entities with zero observers
                if (_hibernation != null)
                {
                    HibernateUnobserved(world, allObjects);
                }
            }
        }

        private readonly HashSet<string> _wokenBuffer = new();

        private void WakeNearbyHibernated(IGameWorldManager3D world, IReadOnlyList<ITypelessWorldObject> allObjects)
        {
            if (_hibernation == null || _hibernation.Count == 0) return;

            _wokenBuffer.Clear();

            for (int i = 0; i < allObjects.Count; i++)
            {
                if (allObjects[i] is not IWorldObject3D observer) continue;
                if (string.IsNullOrEmpty(observer.ClientId)) continue;

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
                // Skip players (observers) — only hibernate non-player entities
                if (!string.IsNullOrEmpty(obj.ClientId)) continue;
                if (obj is not IHibernatable hibernatable) continue;
                if (hibernatable.IsHibernated || !hibernatable.CanHibernate) continue;

                // Check if any observer sees this entity
                _observerCounts.TryGetValue(obj.InstanceId, out var count);
                if (count == 0)
                {
                    // No one sees this entity — hibernate it
                    var pos = obj.Transform.Position;
                    var vnum = 0;
                    // Avoid reflection — use interface or known types
                    if (obj is IVnumProvider vnumProvider)
                        vnum = vnumProvider.Vnum;

                    var zoneName = obj.ZoneId ?? "";

                    world.DestroyObject(obj);
                    _hibernation.Hibernate(obj.InstanceId, zoneName, pos.X, pos.Y, pos.Z, vnum, hibernatable);
                }
            }
        }

        // Reusable scratch collections — cleared per observer, never re-allocated
        private readonly Dictionary<string, HashSet<string>> _scratchVisible = new();
        private readonly List<string> _removeBuffer = new();

        private void UpdateVisibilityFor(
            IWorldObject3D observer,
            IGameWorldManager3D world,
            int worldIndex,
            IReadOnlyList<ITypelessWorldObject> allObjects,
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup)
        {
            var clientId = observer.ClientId;
            var pos = observer.Transform.Position;

            // Reuse per-observer scratch set
            if (!_scratchVisible.TryGetValue(clientId, out var currentlyVisible))
            {
                currentlyVisible = new HashSet<string>();
                _scratchVisible[clientId] = currentlyVisible;
            }
            else
            {
                currentlyVisible.Clear();
            }

            float rangeSq = ViewRange * ViewRange;

            for (int i = 0; i < allObjects.Count; i++)
            {
                if (allObjects[i] is not IWorldObject3D target) continue;
                if (target.InstanceId == observer.InstanceId)
                    continue;

                var tp = target.Transform.Position;
                float dx = tp.X - pos.X;
                float dy = tp.Y - pos.Y;

                if (dx * dx + dy * dy <= rangeSq)
                {
                    currentlyVisible.Add(target.InstanceId);

                    // Track observer count for hibernation — avoid lambda allocation
                    if (_observerCounts.TryGetValue(target.InstanceId, out var count))
                        _observerCounts[target.InstanceId] = count + 1;
                    else
                        _observerCounts[target.InstanceId] = 1;
                }
            }

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

            // Entities that just became invisible — reuse removal buffer
            _removeBuffer.Clear();
            foreach (var instanceId in previouslyVisible)
            {
                if (!currentlyVisible.Contains(instanceId))
                {
                    _removeBuffer.Add(instanceId);
                    if (lookup.TryGetValue(instanceId, out var target))
                    {
                        OnEntityInvisible?.Invoke(new VisibilityChange
                        {
                            ObserverClientId = clientId,
                            Target = target,
                            WorldIndex = worldIndex,
                        });

                        _collisionDispatcher?.Dispatch(observer, target, typeof(Physx.EntityInvisible));
                    }
                }
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                previouslyVisible.Remove(_removeBuffer[i]);
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

        public void RefreshObserver(string clientId)
        {
            _visibleSets.TryRemove(clientId, out _);
        }

        public void RemoveObserver(string clientId)
        {
            if (_visibleSets.TryRemove(clientId, out var visible) && visible.Count > 0)
            {
                if (_organizer is null) { _observers.TryRemove(clientId, out _); return; }
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

            _observers.TryRemove(clientId, out _);
        }
    }
}
