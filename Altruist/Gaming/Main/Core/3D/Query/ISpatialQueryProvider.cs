/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Result of a spatial query (capsule cast or ray cast).
/// Backend-agnostic — works with both physics (BEPU) and heightmap.
/// </summary>
public struct SpatialHit
{
    /// <summary>Distance along the cast direction (0 = at origin, 1 = at maxDistance).</summary>
    public float T;

    /// <summary>World-space point of contact.</summary>
    public Vector3 Point;

    /// <summary>Surface normal at the hit point.</summary>
    public Vector3 Normal;

    /// <summary>The object that was hit (entity, terrain, etc.). May be null for terrain hits.</summary>
    public object? HitObject;
}

/// <summary>
/// Abstraction over spatial queries (capsule cast, ray cast).
/// Two backends auto-selected via DI:
/// - PhysicsSpatialQueryProvider: wraps BEPU engine (physics ON)
/// - HeightmapSpatialQueryProvider: math-based queries against heightmap + colliders (physics OFF)
///
/// KinematicCharacterController3D uses this interface — works identically with either backend.
/// </summary>
public interface ISpatialQueryProvider
{
    IEnumerable<SpatialHit> CapsuleCast(
        Vector3 center,
        float radius,
        float halfLength,
        Vector3 direction,
        float maxDistance,
        int maxHits = 8,
        uint layerMask = uint.MaxValue);

    IEnumerable<SpatialHit> RayCast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        int maxHits = 4,
        uint layerMask = uint.MaxValue);
}
