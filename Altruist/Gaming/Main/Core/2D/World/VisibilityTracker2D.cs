/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.TwoD
{
    /// <summary>Default 2D <see cref="IVisibilityTracker"/> (singleton; registered when <c>altruist:game</c>
    /// exists and <c>altruist:environment:mode</c> is <c>2D</c>). Ticked by <see cref="GameWorldOrganizer2D"/>
    /// after physics; each tick, for every registered observer, every object of the world within
    /// <see cref="ViewRange"/> (Euclidean, inclusive, observer itself excluded) is visible, and the diff
    /// against the previous tick fires <see cref="OnEntityVisible"/> / <see cref="OnEntityInvisible"/>
    /// (synchronously, on the tick thread). Brute force O(observers x objects) per world; it does not use
    /// partitions. The 3D counterpart is <see cref="Altruist.Gaming.ThreeD.VisibilityTracker3D"/>.
    /// <para>Observers are dropped automatically when their object leaves the world or loses its
    /// <c>ClientId</c>.</para>
    /// <example><code>
    /// tracker.OnEntityVisible += c =&gt; SendSpawn(c.ObserverClientId, c.Target);
    /// tracker.Observe(playerObject);           // on join (player.ClientId must be set)
    /// tracker.RemoveObserver(clientId);        // on disconnect
    /// </code></example></summary>
    [Service(typeof(IVisibilityTracker))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [ConditionalOnConfig("altruist:game")]
    public class VisibilityTracker2D : IVisibilityTracker
    {
        private IGameWorldOrganizer2D? _organizer;
        private readonly ConcurrentDictionary<string, HashSet<string>> _visibleSets = new();
        private readonly ConcurrentDictionary<string, IWorldObject2D> _observers = new();
        private readonly ConcurrentDictionary<string, string> _observerInstanceIds = new();

        /// <summary>View radius in world units (config <c>altruist:game:visibility:range</c>, default 5000).</summary>
        public float ViewRange { get; set; } = 5000f;

        /// <inheritdoc/>
        public event Action<VisibilityChange>? OnEntityVisible;
        /// <inheritdoc/>
        public event Action<VisibilityChange>? OnEntityInvisible;

        /// <summary>DI constructor.</summary>
        /// <param name="viewRange">Config <c>altruist:game:visibility:range</c> (default 5000 world units).</param>
        public VisibilityTracker2D(
            [AppConfigValue("altruist:game:visibility:range", "5000")] float viewRange = 5000f)
        {
            ViewRange = viewRange;
        }

        /// <summary>Sets the organizer whose worlds are scanned (normally done by <see cref="WireOrganizer"/>).</summary>
        public void SetOrganizer(IGameWorldOrganizer2D organizer) => _organizer = organizer;

        /// <summary>
        /// Wires the tracker &lt;-&gt; organizer pair after both exist (a constructor dependency in both
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
            var worlds = _organizer.GetAllWorlds()
                .Select(world =>
                {
                    var objects = world.FindAllObjects<IWorldObject2D>().ToList();
                    return (Index: world.Index.Index, Objects: objects, Lookup: objects.ToDictionary(o => o.InstanceId, o => o));
                })
                .ToList();

            // An observer is in exactly one world; it is dropped only when no world holds it.
            foreach (var (clientId, registeredObserver) in _observers.ToArray())
            {
                var home = worlds.FindIndex(w => w.Lookup.ContainsKey(registeredObserver.InstanceId));
                if (home < 0)
                {
                    RemoveObserver(clientId);
                    continue;
                }

                var (worldIndex, allObjects, lookup) = worlds[home];
                var observer = lookup[registeredObserver.InstanceId];
                if (string.IsNullOrEmpty(observer.ClientId))
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

        /// <summary>Registers a 2D world object with a non-empty <c>ClientId</c> as an observer (keyed by
        /// client id, replacing any previous object for that client) and resets its visible set, so the
        /// next tick fires <see cref="OnEntityVisible"/> for everything in range.</summary>
        /// <returns>False when the object is not an <see cref="IWorldObject2D"/> or has no client id.</returns>
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

        /// <inheritdoc/>
        public void RefreshObserver(string clientId)
        {
            _visibleSets.TryRemove(clientId, out _);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <summary>The instance ids visible to the client after the last tick, or null when it is not
        /// observing. The returned set is live (mutated by the next tick); copy it to keep or use it off the tick thread.</summary>
        public IReadOnlySet<string>? GetVisibleEntities(string clientId)
        {
            return _visibleSets.TryGetValue(clientId, out var set) ? set : null;
        }

        /// <summary>Client ids whose visible set contains the entity (lazy scan over all observers).</summary>
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
    }
}
