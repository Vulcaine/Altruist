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
/// <summary>
/// Provides terrain/walkability data for the HeightmapSpatialQueryProvider.
/// Implement in your game to feed walkability grids, heightmaps, etc.
/// </summary>
public interface ITerrainProvider
{
    /// <summary>Check if a position is walkable.</summary>
    bool IsWalkable(float x, float y, float z);

    /// <summary>Get terrain height at a position. Returns 0 if no heightmap.</summary>
    float GetHeight(float x, float z);

    /// <summary>Get terrain normal at a position.</summary>
    Vector3 GetNormal(float x, float z) => Vector3.UnitY;
}

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
