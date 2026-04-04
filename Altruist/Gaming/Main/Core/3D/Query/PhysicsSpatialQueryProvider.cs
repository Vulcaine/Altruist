/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Physics-backed spatial queries using BEPU engine.
/// Registered when altruist:game:physics:enabled = true.
/// </summary>
[Service(typeof(ISpatialQueryProvider))]
[ConditionalOnConfig("altruist:game:physics:enabled", "true")]
public sealed class PhysicsSpatialQueryProvider : ISpatialQueryProvider
{
    private readonly IGameWorldOrganizer3D _worlds;

    public PhysicsSpatialQueryProvider(IGameWorldOrganizer3D worlds)
    {
        _worlds = worlds;
    }

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
