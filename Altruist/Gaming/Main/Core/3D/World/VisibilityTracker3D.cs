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
        private readonly IGameWorldOrganizer3D _organizer;
        private readonly ConcurrentDictionary<string, HashSet<string>> _visibleSets = new();
        private readonly ConcurrentDictionary<string, IWorldObject3D> _observers = new();

        public float ViewRange { get; set; } = 5000f;

        public event Action<VisibilityChange>? OnEntityVisible;
        public event Action<VisibilityChange>? OnEntityInvisible;

        public VisibilityTracker3D(
            IGameWorldOrganizer3D organizer,
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f)
        {
            _organizer = organizer;
            ViewRange = viewRange;
        }

        /// <summary>
        /// Called each tick by the world organizer after all objects have stepped.
        /// Computes visibility diffs for every observer.
        /// </summary>
        public void Tick()
        {
            foreach (var world in _organizer.GetAllWorlds())
            {
                var allObjects = world.FindAllObjects<IWorldObject3D>().ToList();
                var worldIndex = world.Index.Index;

                foreach (var obj in allObjects)
                {
                    if (string.IsNullOrEmpty(obj.ClientId))
                        continue;

                    // This is an observer (has a client connection)
                    _observers[obj.ClientId] = obj;
                    UpdateVisibilityFor(obj, world, worldIndex, allObjects);
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

        public void RefreshObserver(string clientId)
        {
            // Clear known set so next tick sends all nearby as "visible"
            _visibleSets.TryRemove(clientId, out _);
        }

        public void RemoveObserver(string clientId)
        {
            if (_visibleSets.TryRemove(clientId, out var visible) && visible.Count > 0)
            {
                // Fire invisible events for all previously visible entities
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

        public IReadOnlySet<string>? GetVisibleEntities(string clientId)
        {
            return _visibleSets.TryGetValue(clientId, out var set) ? set : null;
        }
    }
}
