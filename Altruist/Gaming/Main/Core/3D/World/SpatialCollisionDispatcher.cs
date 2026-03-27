/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using Altruist.Gaming.ThreeD;
using Altruist.Physx;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Physics-less collision detection with full lifecycle:
/// Enter (first overlap), Stay (continuous), Exit (end overlap), Hit (one-shot).
/// Works for entity-entity, entity-zone, entity-partition.
/// Uses the same [CollisionHandler]/[CollisionEvent] API as the physics system.
/// </summary>
public interface ISpatialCollisionDispatcher
{
    /// <summary>One-shot hit dispatch (combat). No enter/stay/exit tracking.</summary>
    void DispatchHit(object entityA, object entityB);

    /// <summary>Dispatch a specific event phase between two objects.</summary>
    void Dispatch(object entityA, object entityB, Type eventType);

    /// <summary>Run full overlap detection tick (enter/stay/exit) for a world.</summary>
    void Tick(IGameWorldManager3D world, float collisionRadius = 200f);

    int HandlerCount { get; }
}

[Service(typeof(ISpatialCollisionDispatcher))]
public sealed class SpatialCollisionDispatcher : ISpatialCollisionDispatcher
{
    private readonly ILogger _logger;

    // Active entity-entity overlaps: long hash key -> (objA, objB)
    // Uses combined hash of ordered InstanceIds to avoid tuple allocation
    private readonly ConcurrentDictionary<long, (object, object)> _activeOverlaps = new();

    // Reverse index: entity instanceId -> set of pair hashes it participates in
    private readonly ConcurrentDictionary<string, HashSet<long>> _entityPairs = new();

    // Entity -> current zone name + zone object (for zone enter/exit)
    private readonly ConcurrentDictionary<string, (string ZoneName, object ZoneObj)> _entityZones = new();

    // Reusable per-tick scratch collections — long keys avoid tuple allocation
    private readonly HashSet<long> _currentOverlaps = new();
    private readonly List<long> _exitBuffer = new();

    // Spatial broadphase grid for O(n) instead of O(n²) pair detection
    private readonly SpatialHashGrid _grid = new(cellSize: 300f);
    private readonly List<int> _nearbyBuffer = new(64);

    public int HandlerCount => CollisionHandlerRegistry.TotalHandlerCount;

    public SpatialCollisionDispatcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SpatialCollisionDispatcher>();
    }

    public void DispatchHit(object entityA, object entityB)
    {
        Dispatch(entityA, entityB, typeof(CollisionHit));
    }

    public void Dispatch(object entityA, object entityB, Type eventType)
    {
        var handlers = CollisionHandlerRegistry.GetHandlers(
            entityA.GetType(), entityB.GetType(), eventType);

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<object, object>)handler.Invoker)(entityA, entityB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Collision handler {Handler} failed for {Event}({A}, {B})",
                    handler.HandlerType.Name, eventType.Name,
                    entityA.GetType().Name, entityB.GetType().Name);
            }
        }
    }

    public void Tick(IGameWorldManager3D world, float collisionRadius = 200f)
    {
        _currentOverlaps.Clear();
        var (allObjects, _) = world.GetCachedSnapshot();

        // Build spatial broadphase grid (O(n)) — avoids O(n²) pair checking
        _grid.Build(allObjects);

        // -- Entity <-> Entity overlap detection via broadphase --
        for (int i = 0; i < allObjects.Count; i++)
        {
            var objA = allObjects[i];
            var posA = objA.Transform.Position;
            var radiusA = GetColliderRadius(objA, collisionRadius);

            // Query only nearby entities from the spatial grid
            _grid.QueryRadius(posA.X, posA.Y, MathF.Max(radiusA, collisionRadius), _nearbyBuffer);

            for (int n = 0; n < _nearbyBuffer.Count; n++)
            {
                var j = _nearbyBuffer[n];
                if (j <= i) continue; // Avoid duplicate pairs (same as j = i + 1)

                var objB = allObjects[j];

                // Layer filtering: entities only collide if their layers overlap
                if ((objA.CollisionLayer & objB.CollisionLayer) == 0)
                    continue;

                if (!CollisionHandlerRegistry.HasHandlers(objA.GetType(), objB.GetType()))
                    continue;

                var radiusB = GetColliderRadius(objB, collisionRadius);
                var totalRadius = MathF.Max(radiusA, radiusB);

                var posB = objB.Transform.Position;
                var dx = posA.X - posB.X;
                var dy = posA.Y - posB.Y;
                var dz = posA.Z - posB.Z;

                if (dx * dx + dy * dy + dz * dz > totalRadius * totalRadius)
                    continue;

                var pair = PairHash(objA.InstanceId, objB.InstanceId);
                _currentOverlaps.Add(pair);

                if (!_activeOverlaps.ContainsKey(pair))
                {
                    _activeOverlaps[pair] = (objA, objB);
                    TrackPair(objA.InstanceId, objB.InstanceId, pair);
                    Dispatch(objA, objB, typeof(CollisionEnter));
                }
                else
                {
                    Dispatch(objA, objB, typeof(CollisionStay));
                }
            }
        }

        // Fire Exit for pairs no longer overlapping
        _exitBuffer.Clear();
        foreach (var kvp in _activeOverlaps)
        {
            if (!_currentOverlaps.Contains(kvp.Key))
            {
                var (objA, objB) = kvp.Value;
                Dispatch(objA, objB, typeof(CollisionExit));
                _exitBuffer.Add(kvp.Key);
            }
        }
        for (int i = 0; i < _exitBuffer.Count; i++)
        {
            _activeOverlaps.TryRemove(_exitBuffer[i], out _);
            UntrackPair(_exitBuffer[i]);
        }

        // -- Entity <-> Zone detection --
        TickZoneOverlaps(world, allObjects);
    }

    private void TickZoneOverlaps(IGameWorldManager3D world, IReadOnlyList<IWorldObject3D> allObjects)
    {
        // Zone detection requires ZoneManager3D with FindZoneAt
        if (world is not GameWorldManager3D gw) return;

        var zones = gw.Zones as ZoneManager3D;
        if (zones == null) return;

        foreach (var obj in allObjects)
        {
            var pos = obj.Transform.Position;
            var currentZone = zones.FindZoneAt((int)pos.X, (int)pos.Y, (int)pos.Z);
            var currentZoneName = (currentZone as IZone)?.Name;

            _entityZones.TryGetValue(obj.InstanceId, out var prev);

            if (currentZoneName != prev.ZoneName)
            {
                // Zone changed
                if (prev.ZoneObj != null)
                    Dispatch(obj, prev.ZoneObj, typeof(CollisionExit));

                if (currentZone != null)
                {
                    Dispatch(obj, currentZone, typeof(CollisionEnter));
                    _entityZones[obj.InstanceId] = (currentZoneName!, currentZone);
                }
                else
                {
                    _entityZones.TryRemove(obj.InstanceId, out _);
                }
            }
        }
    }

    private void TrackPair(string idA, string idB, long pairHash)
    {
        _entityPairs.GetOrAdd(idA, static _ => new HashSet<long>()).Add(pairHash);
        _entityPairs.GetOrAdd(idB, static _ => new HashSet<long>()).Add(pairHash);
    }

    private void UntrackPair(long pairHash)
    {
        // Clean reverse index when pair exits
        foreach (var (_, pairSet) in _entityPairs)
            pairSet.Remove(pairHash);
    }

    /// <summary>Remove tracking for a destroyed entity.</summary>
    public void RemoveEntity(string instanceId)
    {
        _entityZones.TryRemove(instanceId, out _);

        if (_entityPairs.TryRemove(instanceId, out var pairs))
        {
            foreach (var pairHash in pairs)
                _activeOverlaps.TryRemove(pairHash, out _);
        }
    }

    /// <summary>
    /// Extract collision radius from entity's ColliderDescriptors.
    /// Uses the first collider's size. Falls back to defaultRadius if no colliders.
    /// Same shape data used by physics when enabled.
    /// </summary>
    private static float GetColliderRadius(IWorldObject3D obj, float defaultRadius)
    {
        var colliders = obj.ColliderDescriptors;
        if (colliders == null) return defaultRadius;

        foreach (var collider in colliders)
        {
            var size = collider.Transform.Size;
            // Sphere: X = radius. Box: max dimension. Capsule: X = radius.
            var maxExtent = MathF.Max(MathF.Max(size.X, size.Y), size.Z);
            if (maxExtent > 0) return maxExtent;
        }

        return defaultRadius;
    }

    /// <summary>
    /// Produces a deterministic long hash for an ordered pair of instance IDs.
    /// Avoids string tuple allocation. Collision probability is negligible for game entity counts.
    /// </summary>
    private static long PairHash(string a, string b)
    {
        int ha, hb;
        if (string.CompareOrdinal(a, b) <= 0)
        {
            ha = a.GetHashCode();
            hb = b.GetHashCode();
        }
        else
        {
            ha = b.GetHashCode();
            hb = a.GetHashCode();
        }
        return ((long)ha << 32) | (uint)hb;
    }
}
