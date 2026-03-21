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
        private readonly ConcurrentDictionary<string, HashSet<string>> _visibleSets = new();
        private readonly ConcurrentDictionary<string, IWorldObject3D> _observers = new();

        // Tracks how many observers see each non-player entity
        private readonly ConcurrentDictionary<string, int> _observerCounts = new();

        public float ViewRange { get; set; } = 5000f;

        public event Action<VisibilityChange>? OnEntityVisible;
        public event Action<VisibilityChange>? OnEntityInvisible;

        public VisibilityTracker3D(
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f,
            IEntityHibernationService? hibernation = null)
        {
            ViewRange = viewRange;
            _hibernation = hibernation;
        }

        public void SetOrganizer(IGameWorldOrganizer3D organizer) => _organizer = organizer;

        public void Tick()
        {
            if (_organizer is null) return;

            foreach (var world in _organizer.GetAllWorlds())
            {
                var allObjects = world.FindAllObjects<IWorldObject3D>().ToList();
                var worldIndex = world.Index.Index;

                // Reset observer counts for this tick
                _observerCounts.Clear();

                // Phase 1: Wake hibernated entities near any observer
                if (_hibernation != null)
                {
                    WakeNearbyHibernated(world, allObjects);
                }

                // Phase 2: Compute visibility for each observer (player)
                foreach (var obj in allObjects)
                {
                    if (string.IsNullOrEmpty(obj.ClientId))
                        continue;

                    _observers[obj.ClientId] = obj;
                    UpdateVisibilityFor(obj, world, worldIndex, allObjects);
                }

                // Phase 3: Hibernate entities with zero observers
                if (_hibernation != null)
                {
                    HibernateUnobserved(world, allObjects);
                }
            }
        }

        private void WakeNearbyHibernated(IGameWorldManager3D world, List<IWorldObject3D> allObjects)
        {
            if (_hibernation == null || _hibernation.Count == 0) return;

            // Collect all observer positions
            var observers = allObjects.Where(o => !string.IsNullOrEmpty(o.ClientId)).ToList();
            if (observers.Count == 0) return;

            var woken = new HashSet<string>();

            foreach (var observer in observers)
            {
                var pos = observer.Transform.Position;
                var nearby = _hibernation.FindNearby(pos.X, pos.Y, pos.Z, ViewRange);

                foreach (var hibernated in nearby)
                {
                    if (woken.Contains(hibernated.InstanceId)) continue;

                    var entry = _hibernation.Wake(hibernated.InstanceId);
                    if (entry?.Entity is IWorldObject3D worldObj)
                    {
                        // Re-insert into world
                        try
                        {
                            world.SpawnDynamicObject(worldObj).GetAwaiter().GetResult();
                            allObjects.Add(worldObj);
                            woken.Add(hibernated.InstanceId);
                        }
                        catch { /* entity may already exist */ }
                    }
                }
            }
        }

        private void HibernateUnobserved(IGameWorldManager3D world, List<IWorldObject3D> allObjects)
        {
            if (_hibernation == null) return;

            foreach (var obj in allObjects)
            {
                // Skip players (observers) — only hibernate non-player entities
                if (!string.IsNullOrEmpty(obj.ClientId)) continue;
                if (obj is not IHibernatable hibernatable) continue;
                if (hibernatable.IsHibernated || !hibernatable.CanHibernate) continue;

                // Check if any observer sees this entity
                var count = _observerCounts.GetValueOrDefault(obj.InstanceId, 0);
                if (count == 0)
                {
                    // No one sees this entity — hibernate it
                    var pos = obj.Transform.Position;
                    var vnum = 0; // Game layer can store vnum in the entity
                    if (obj is IWorldObject3D { } wo && wo.GetType().GetProperty("Vnum") is { } vnumProp)
                    {
                        var val = vnumProp.GetValue(wo);
                        if (val is uint uv) vnum = (int)uv;
                        else if (val is int iv) vnum = iv;
                    }

                    var zoneName = obj.ZoneId ?? "";

                    world.DestroyObject(obj);
                    _hibernation.Hibernate(obj.InstanceId, zoneName, pos.X, pos.Y, pos.Z, vnum, hibernatable);
                }
            }
        }

        private void UpdateVisibilityFor(
            IWorldObject3D observer,
            IGameWorldManager3D world,
            int worldIndex,
            List<IWorldObject3D> allObjects)
        {
            var clientId = observer.ClientId;
            var pos = observer.Transform.Position;

            var currentlyVisible = new HashSet<string>();
            float rangeSq = ViewRange * ViewRange;

            foreach (var target in allObjects)
            {
                if (target.InstanceId == observer.InstanceId)
                    continue;

                var tp = target.Transform.Position;
                float dx = tp.X - pos.X;
                float dy = tp.Y - pos.Y;

                if (dx * dx + dy * dy <= rangeSq)
                {
                    currentlyVisible.Add(target.InstanceId);

                    // Track observer count for hibernation
                    _observerCounts.AddOrUpdate(target.InstanceId, 1, (_, c) => c + 1);
                }
            }

            var previouslyVisible = _visibleSets.GetOrAdd(clientId, _ => new HashSet<string>());

            // Entities that just became visible
            foreach (var instanceId in currentlyVisible)
            {
                if (previouslyVisible.Add(instanceId))
                {
                    var target = allObjects.Find(o => o.InstanceId == instanceId);
                    if (target != null)
                    {
                        OnEntityVisible?.Invoke(new VisibilityChange
                        {
                            ObserverClientId = clientId,
                            Target = target,
                            WorldIndex = worldIndex,
                        });
                    }
                }
            }

            // Entities that just became invisible
            var toRemove = new List<string>();
            foreach (var instanceId in previouslyVisible)
            {
                if (!currentlyVisible.Contains(instanceId))
                {
                    toRemove.Add(instanceId);
                    var target = allObjects.Find(o => o.InstanceId == instanceId);
                    if (target != null)
                    {
                        OnEntityInvisible?.Invoke(new VisibilityChange
                        {
                            ObserverClientId = clientId,
                            Target = target,
                            WorldIndex = worldIndex,
                        });
                    }
                }
            }

            foreach (var id in toRemove)
                previouslyVisible.Remove(id);
        }

        public IReadOnlySet<string>? GetVisibleEntities(string clientId)
        {
            return _visibleSets.TryGetValue(clientId, out var set) ? set : null;
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
                    var allObjects = world.FindAllObjects<IWorldObject3D>().ToList();
                    foreach (var instanceId in visible)
                    {
                        var target = allObjects.Find(o => o.InstanceId == instanceId);
                        if (target != null)
                        {
                            OnEntityInvisible?.Invoke(new VisibilityChange
                            {
                                ObserverClientId = clientId,
                                Target = target,
                                WorldIndex = world.Index.Index,
                            });
                        }
                    }
                }
            }

            _observers.TryRemove(clientId, out _);
        }
    }
}
