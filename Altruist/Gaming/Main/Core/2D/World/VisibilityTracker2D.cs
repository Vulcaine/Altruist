/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.TwoD
{
    [Service(typeof(IVisibilityTracker))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [ConditionalOnConfig("altruist:game")]
    public class VisibilityTracker2D : IVisibilityTracker
    {
        private IGameWorldOrganizer2D? _organizer;
        private readonly ConcurrentDictionary<string, HashSet<string>> _visibleSets = new();
        private readonly ConcurrentDictionary<string, IWorldObject2D> _observers = new();
        private readonly ConcurrentDictionary<string, string> _observerInstanceIds = new();

        public float ViewRange { get; set; } = 5000f;

        public event Action<VisibilityChange>? OnEntityVisible;
        public event Action<VisibilityChange>? OnEntityInvisible;

        public VisibilityTracker2D(
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f)
        {
            ViewRange = viewRange;
        }

        public void SetOrganizer(IGameWorldOrganizer2D organizer) => _organizer = organizer;

        /// <summary>
        /// Wires the tracker <-> organizer pair after both exist (a constructor dependency in both
        /// directions is a DI cycle). Mirrors VisibilityTracker3D.WireOrganizer.
        /// </summary>
        [PostConstruct]
        public void WireOrganizer(IGameWorldOrganizer2D organizer)
        {
            if (_organizer is not null) return;
            organizer.SetVisibilityTracker(this);
            SetOrganizer(organizer);
        }

        /// <summary>
        /// Called each tick by the world organizer after all objects have stepped.
        /// </summary>
        public void Tick()
        {
            if (_organizer is null) return;
            foreach (var world in _organizer.GetAllWorlds())
            {
                var allObjects = world.FindAllObjects<IWorldObject2D>().ToList();
                var lookup = allObjects.ToDictionary(o => o.InstanceId, o => o);
                var worldIndex = world.Index.Index;

                foreach (var (clientId, registeredObserver) in _observers.ToArray())
                {
                    if (!lookup.TryGetValue(registeredObserver.InstanceId, out var observer) ||
                        string.IsNullOrEmpty(observer.ClientId))
                    {
                        RemoveObserver(clientId);
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
                    UpdateVisibilityFor(observer, worldIndex, allObjects);
                }
            }
        }

        private void UpdateVisibilityFor(
            IWorldObject2D observer,
            int worldIndex,
            List<IWorldObject2D> allObjects)
        {
            var clientId = observer.ClientId;
            var pos = observer.Transform.Position;

            var currentlyVisible = AltruistPool.RentHashSet<string>();
            float rangeSq = ViewRange * ViewRange;

            foreach (var target in allObjects)
            {
                if (target.InstanceId == observer.InstanceId)
                    continue;

                var tp = target.Transform.Position;
                float dx = tp.X - pos.X;
                float dy = tp.Y - pos.Y;

                if (dx * dx + dy * dy <= rangeSq)
                    currentlyVisible.Add(target.InstanceId);
            }

            var previouslyVisible = _visibleSets.GetOrAdd(clientId, _ => new HashSet<string>());

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

            var toRemove = AltruistPool.RentList<string>();
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

            AltruistPool.ReturnHashSet(currentlyVisible);
            AltruistPool.ReturnList(toRemove);
        }

        public bool Observe(ITypelessWorldObject observer)
        {
            if (observer is not IWorldObject2D worldObject)
                return false;

            if (string.IsNullOrEmpty(worldObject.ClientId))
                return false;

            _observers[worldObject.ClientId] = worldObject;
            _observerInstanceIds[worldObject.InstanceId] = worldObject.ClientId;
            RefreshObserver(worldObject.ClientId);
            return true;
        }

        public void RefreshObserver(string clientId)
        {
            _visibleSets.TryRemove(clientId, out _);
        }

        public void RemoveObserver(ITypelessWorldObject observer)
        {
            if (observer is not IWorldObject2D worldObject)
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
            if (_visibleSets.TryRemove(clientId, out var visible) && visible.Count > 0 && _organizer is not null)
            {
                foreach (var world in _organizer.GetAllWorlds())
                {
                    var allObjects = world.FindAllObjects<IWorldObject2D>().ToList();
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

            if (_observers.TryRemove(clientId, out var observer))
                _observerInstanceIds.TryRemove(observer.InstanceId, out _);
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
    }
}
