/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Result of a spatial query (capsule cast or ray cast) from an <see cref="ISpatialQueryProvider"/>.
/// Backend-agnostic: produced by both <see cref="PhysicsSpatialQueryProvider"/> and <see cref="HeightmapSpatialQueryProvider"/>.
/// </summary>
public struct SpatialHit
{
    /// <summary>Distance from the cast origin to the hit along the cast direction, in world units (0 .. maxDistance; 0 can mean the shape started overlapping).</summary>
    public float T;

    /// <summary>World-space point of contact.</summary>
    public Vector3 Point;

    /// <summary>Surface normal at the hit point.</summary>
    public Vector3 Normal;

    /// <summary>What was hit: an <see cref="Altruist.Physx.ThreeD.IPhysxBody3D"/> for the physics backend, an <see cref="IWorldObject3D"/> for the heightmap backend, or null for heightmap terrain hits.</summary>
    public object? HitObject;
}

/// <summary>
/// Game-supplied terrain data (height, walkability, normal) consumed by <see cref="HeightmapSpatialQueryProvider"/>, the
/// navmesh builder and the terrain-snapping body navigation helpers.
/// </summary>
/// <remarks>
/// Implement it in your game (heightmap, walkability grid, ...) and register it in DI when running without a physics engine;
/// <see cref="HeightmapSpatialQueryProvider"/> takes it as an optional dependency. Coordinates are world units, +Y up;
/// implementations should be cheap and thread-safe for reads because they are sampled every tick.
/// </remarks>
public interface ITerrainProvider
{
    /// <summary>Returns whether a character may stand at / move through this world position.</summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y (may be ignored by 2.5D grids).</param>
    /// <param name="z">World Z.</param>
    bool IsWalkable(float x, float y, float z);

    /// <summary>Returns the terrain surface Y at a horizontal position (0 when the game has no heightmap).</summary>
    /// <param name="x">World X.</param>
    /// <param name="z">World Z.</param>
    float GetHeight(float x, float z);

    /// <summary>Returns the terrain surface normal at a horizontal position. Default implementation returns +Y (flat).</summary>
    /// <param name="x">World X.</param>
    /// <param name="z">World Z.</param>
    Vector3 GetNormal(float x, float z) => Vector3.UnitY;
}

/// <summary>
/// Backend-agnostic spatial queries (capsule cast, ray cast) against world 0, so gameplay code works whether or not a physics
/// engine is running.
/// </summary>
/// <remarks>
/// <para>Exactly one implementation is registered as a DI singleton, selected by config key
/// <c>altruist:game:physics:enabled</c>: <see cref="PhysicsSpatialQueryProvider"/> (<c>true</c>, wraps the physics engine of world
/// index 0) or <see cref="HeightmapSpatialQueryProvider"/> (<c>false</c>, approximate math against an optional
/// <see cref="ITerrainProvider"/> plus world-object colliders). Inject this interface rather than a concrete backend.</para>
/// <para>When you need engine-specific results (the hit <see cref="Altruist.Physx.ThreeD.IPhysxBody3D"/>, multiple worlds), call
/// <see cref="Altruist.Physx.ThreeD.IPhysxWorldEngine3D"/> via <see cref="IGameWorldManager3D.PhysxWorld"/> directly.
/// <see cref="KinematicCharacterController3D.SetQueryProvider"/> accepts this interface.</para>
/// <para>Backends differ in fidelity: the heightmap backend ignores <c>layerMask</c> and the capsule's half length, and
/// treats every collider as a sphere.</para>
/// </remarks>
/// <example><code>
/// public sealed class LineOfSight(ISpatialQueryProvider queries)
/// {
///     public bool Blocked(Vector3 from, Vector3 to)
///     {
///         var d = to - from; var len = d.Length();
///         return queries.RayCast(from, d / len, len, maxHits: 1).Any();
///     }
/// }
/// </code></example>
public interface ISpatialQueryProvider
{
    /// <summary>Sweeps an upright (Y-axis) capsule and returns what it would hit, nearest first.</summary>
    /// <param name="center">Capsule centre at the start of the sweep (world space).</param>
    /// <param name="radius">Capsule radius.</param>
    /// <param name="halfLength">Half the distance between the hemisphere centres.</param>
    /// <param name="direction">Sweep direction; pass a unit vector (the heightmap backend does not normalise it).</param>
    /// <param name="maxDistance">Sweep distance in world units.</param>
    /// <param name="maxHits">Maximum number of hits to return.</param>
    /// <param name="layerMask">Physics layer filter (<see cref="Altruist.Physx.Contracts.PhysxLayer"/> bits); ignored by the heightmap backend.</param>
    /// <returns>Hits, possibly empty (also empty when world 0 or its physics engine does not exist).</returns>
    IEnumerable<SpatialHit> CapsuleCast(
        Vector3 center,
        float radius,
        float halfLength,
        Vector3 direction,
        float maxDistance,
        int maxHits = 8,
        uint layerMask = uint.MaxValue);

    /// <summary>Casts a ray and returns what it hits.</summary>
    /// <param name="origin">Ray start in world space.</param>
    /// <param name="direction">Unit direction.</param>
    /// <param name="maxDistance">Ray length in world units.</param>
    /// <param name="maxHits">Maximum number of hits to return.</param>
    /// <param name="layerMask">Physics layer filter; ignored by the heightmap backend.</param>
    /// <returns>Hits, possibly empty. The physics backend orders them by distance; the heightmap backend yields the terrain hit first, then colliders in world-object order.</returns>
    IEnumerable<SpatialHit> RayCast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        int maxHits = 4,
        uint layerMask = uint.MaxValue);
}
