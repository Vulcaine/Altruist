/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// <see cref="ISpatialQueryProvider"/> backed by the physics engine (BEPU) of world index 0. Registered as the DI
/// singleton when config <c>altruist:game:physics:enabled</c> is <c>true</c>; otherwise <see cref="HeightmapSpatialQueryProvider"/> is used.
/// </summary>
/// <remarks>
/// Thin adapter over <see cref="IPhysxWorldEngine3D.RayCast"/>/<see cref="IPhysxWorldEngine3D.CapsuleCast"/>: layer masks are
/// honoured, hits are nearest first and <see cref="SpatialHit.HitObject"/> is the hit <see cref="IPhysxBody3D"/>. Returns no
/// hits while world 0 or its physics world does not exist. Query the engine directly for other worlds.
/// </remarks>
[Service(typeof(ISpatialQueryProvider))]
[ConditionalOnConfig("altruist:game:physics:enabled", "true")]
public sealed class PhysicsSpatialQueryProvider : ISpatialQueryProvider
{
    private readonly IGameWorldOrganizer3D _worlds;

    /// <summary>Created by DI.</summary>
    /// <param name="worlds">World organizer; world index 0 is queried.</param>
    public PhysicsSpatialQueryProvider(IGameWorldOrganizer3D worlds)
    {
        _worlds = worlds;
    }

    /// <inheritdoc/>
    public IEnumerable<SpatialHit> CapsuleCast(
        Vector3 center, float radius, float halfLength,
        Vector3 direction, float maxDistance,
        int maxHits = 8, uint layerMask = uint.MaxValue)
    {
        var world = _worlds.GetWorld(0);
        if (world?.PhysxWorld?.Engine == null) yield break;

        var hits = world.PhysxWorld.Engine.CapsuleCast(
            center, radius, halfLength, direction, maxDistance, maxHits, layerMask);

        foreach (var h in hits)
        {
            yield return new SpatialHit
            {
                T = h.T,
                Point = h.Point,
                Normal = h.Normal,
                HitObject = h.Body,
            };
        }
    }

    /// <inheritdoc/>
    /// <remarks>The ray segment is <c>origin .. origin + direction * maxDistance</c>, so <paramref name="direction"/> should be unit length for <see cref="SpatialHit.T"/> to stay within <paramref name="maxDistance"/>.</remarks>
    public IEnumerable<SpatialHit> RayCast(
        Vector3 origin, Vector3 direction,
        float maxDistance, int maxHits = 4, uint layerMask = uint.MaxValue)
    {
        var world = _worlds.GetWorld(0);
        if (world?.PhysxWorld?.Engine == null) yield break;

        var target = origin + direction * maxDistance;
        var ray = new PhysxRay3D(origin, target);
        var hits = world.PhysxWorld.Engine.RayCast(ray, maxHits, layerMask);

        foreach (var h in hits)
        {
            yield return new SpatialHit
            {
                T = h.T,
                Point = h.Point,
                Normal = h.Normal,
                HitObject = h.Body,
            };
        }
    }
}
