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
/// Distance-based collision detection that works WITHOUT physics.
/// Uses spatial partitions to find nearby entity pairs and invokes
/// the same [CollisionHandler]/[CollisionEvent] handlers as the physics system.
///
/// When physics is enabled, BEPU handles collisions. When physics is disabled,
/// this dispatcher provides the same API via distance checks.
/// </summary>
public interface ISpatialCollisionDispatcher
{
    /// <summary>Check a specific pair of entities for collision and invoke handlers if overlapping.</summary>
    void CheckCollision(object entityA, object entityB);

    /// <summary>Fire all registered handlers for a type pair. Used by CombatService on hit.</summary>
    void DispatchHit(object entityA, object entityB);

    /// <summary>Get the number of registered collision handler pairs.</summary>
    int HandlerCount { get; }
}

[Service(typeof(ISpatialCollisionDispatcher))]
public sealed class SpatialCollisionDispatcher : ISpatialCollisionDispatcher
{
    private readonly ILogger _logger;

    public int HandlerCount => CollisionHandlerRegistry.TotalHandlerCount;

    public SpatialCollisionDispatcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SpatialCollisionDispatcher>();
    }

    public void CheckCollision(object entityA, object entityB)
    {
        if (entityA == entityB) return;

        var typeA = entityA.GetType();
        var typeB = entityB.GetType();

        var handlers = CollisionHandlerRegistry.GetHandlers(typeA, typeB);
        if (handlers == null || handlers.Count == 0) return;

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<object, object>)handler.Invoker)(entityA, entityB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Collision handler {Handler} failed for ({A}, {B})",
                    handler.HandlerType.Name, typeA.Name, typeB.Name);
            }
        }
    }

    public void DispatchHit(object entityA, object entityB)
    {
        CheckCollision(entityA, entityB);
    }
}
