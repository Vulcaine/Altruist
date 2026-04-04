/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Heightmap + collider-based spatial queries. No physics engine required.
/// Registered when altruist:game:physics:enabled = false.
///
/// CapsuleCast: checks entity ColliderDescriptors (sphere intersection) + heightmap terrain.
/// RayCast: checks heightmap elevation at ray endpoint.
/// </summary>
[Service(typeof(ISpatialQueryProvider))]
[ConditionalOnConfig("altruist:game:physics:enabled", "false")]
public sealed class HeightmapSpatialQueryProvider : ISpatialQueryProvider
{
    private readonly IGameWorldOrganizer3D _worlds;
    private readonly ITerrainProvider? _terrain;
    private readonly ILogger _logger;

    public HeightmapSpatialQueryProvider(
        IGameWorldOrganizer3D worlds,
        ILoggerFactory loggerFactory,
        ITerrainProvider? terrain = null)
    {
        _worlds = worlds;
        _terrain = terrain;
        _logger = loggerFactory.CreateLogger<HeightmapSpatialQueryProvider>();
    }

    public IEnumerable<SpatialHit> CapsuleCast(
        Vector3 center, float radius, float halfLength,
        Vector3 direction, float maxDistance,
        int maxHits = 8, uint layerMask = uint.MaxValue)
    {
        var world = _worlds.GetWorld(0);
        if (world == null) yield break;

        var dest = center + direction * maxDistance;
        var hits = new List<SpatialHit>();

        // 0. Check terrain walkability along the path
        if (_terrain != null)
        {
            // Sample several points along the cast direction
            int steps = Math.Max(1, (int)(maxDistance / 50f)); // check every ~50 units
            for (int s = 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                var samplePos = center + direction * (maxDistance * t);
                if (!_terrain.IsWalkable(samplePos.X, samplePos.Y, samplePos.Z))
                {
                    // Blocked — return hit at the last walkable point
                    var hitT = maxDistance * Math.Max(0, t - (1f / steps));
                    var hitPoint = center + direction * hitT;
                    hits.Add(new SpatialHit
                    {
                        T = hitT,
                        Point = hitPoint,
                        Normal = -direction, // push back along movement direction
                        HitObject = null, // terrain
                    });
                    break;
                }
            }
        }

        // 1. Check entity colliders (sphere-capsule intersection)
        foreach (var obj in world.FindAllObjects<IWorldObject3D>())
        {
            var colliders = obj.ColliderDescriptors;
            if (colliders == null) continue;

            foreach (var collider in colliders)
            {
                var colliderSize = collider.Transform.Size;
                var colliderRadius = MathF.Max(MathF.Max(colliderSize.X, colliderSize.Y), colliderSize.Z);
                if (colliderRadius <= 0) continue;

                var p = obj.Transform.Position;
                var objPos = new Vector3(p.X, p.Y, p.Z);
                var combinedRadius = radius + colliderRadius;

                // Capsule-sphere intersection: find closest point on ray to sphere center
                var rayOrigin = center;
                var rayDir = direction;
                var toSphere = objPos - rayOrigin;
                var t = Vector3.Dot(toSphere, rayDir);

                if (t < 0 || t > maxDistance) continue;

                var closest = rayOrigin + rayDir * t;
                var dist = Vector3.Distance(closest, objPos);

                if (dist <= combinedRadius)
                {
                    var normal = Vector3.Normalize(closest - objPos);
                    if (normal.LengthSquared() < 0.5f) normal = Vector3.UnitY;

                    hits.Add(new SpatialHit
                    {
                        T = t,
                        Point = closest,
                        Normal = normal,
                        HitObject = obj,
                    });

                    if (hits.Count >= maxHits) break;
                }
            }

            if (hits.Count >= maxHits) break;
        }

        // Sort by distance
        hits.Sort((a, b) => a.T.CompareTo(b.T));

        foreach (var hit in hits)
            yield return hit;
    }

    public IEnumerable<SpatialHit> RayCast(
        Vector3 origin, Vector3 direction,
        float maxDistance, int maxHits = 4, uint layerMask = uint.MaxValue)
    {
        if (direction.Y < -0.01f)
        {
            float groundY = _terrain?.GetHeight(origin.X, origin.Z) ?? 0f;
            float t = (origin.Y - groundY) / -direction.Y;

            if (t >= 0 && t <= maxDistance)
            {
                var point = origin + direction * t;
                yield return new SpatialHit
                {
                    T = t,
                    Point = point,
                    Normal = Vector3.UnitY,
                    HitObject = null, // terrain
                };
            }
        }

        // Also check entity colliders along the ray
        var world = _worlds.GetWorld(0);
        if (world == null) yield break;

        int count = 0;
        foreach (var obj in world.FindAllObjects<IWorldObject3D>())
        {
            var colliders = obj.ColliderDescriptors;
            if (colliders == null) continue;

            foreach (var collider in colliders)
            {
                var colliderSize = collider.Transform.Size;
                var colliderRadius = MathF.Max(MathF.Max(colliderSize.X, colliderSize.Y), colliderSize.Z);
                if (colliderRadius <= 0) continue;

                var p = obj.Transform.Position;
                var objPos = new Vector3(p.X, p.Y, p.Z);
                var toObj = objPos - origin;
                var t = Vector3.Dot(toObj, direction);
                if (t < 0 || t > maxDistance) continue;

                var closest = origin + direction * t;
                var dist = Vector3.Distance(closest, objPos);

                if (dist <= colliderRadius)
                {
                    yield return new SpatialHit
                    {
                        T = t,
                        Point = closest,
                        Normal = Vector3.Normalize(closest - objPos),
                        HitObject = obj,
                    };

                    if (++count >= maxHits) yield break;
                }
            }
        }
    }
}
