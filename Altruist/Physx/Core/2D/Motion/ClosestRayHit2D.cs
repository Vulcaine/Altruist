/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. A reusable ray-cast callback that keeps the closest accepted hit:
/// fixtures rejected by <see cref="Filter"/> are ignored (-1), every accepted hit clips the ray
/// (returns its fraction) and is kept when its distance <c>fraction * Length</c> is below the best
/// so far. Cast several rays into one instance (e.g. from both ends of a body) to get the closest
/// over all of them; <see cref="Reset"/> to reuse it.
/// <para>Use it with <see cref="IPhysxWorldEngine2D.RayCast(Vector2,Vector2,IPhysxRayCastCallback2D)"/>
/// (fixture-level hits, filter per fixture), or call <see cref="RayCastExtensions2D.RayCastClosest"/>
/// for a one-shot. For body-level hits sorted by distance (no filter) use
/// <see cref="IPhysxWorldEngine2D.RayCast(PhysxRay2D,int)"/>.</para>
/// <para>Ties: a later hit at exactly the same distance does not replace the kept one; since the
/// engine reports fixtures in no particular order, equal-distance ties keep whichever came first.
/// Not thread-safe (mutable).</para>
/// <example><code>
/// var hit = new ClosestRayHit2D(Vector2.Distance(a, b), f =&gt; !f.IsTrigger);
/// world.RayCast(a, b, hit);
/// world.RayCast(a2, b2, hit);        // closest over both rays
/// if (hit.Found) Use(hit.Point, hit.Normal, hit.Distance);
/// </code></example></summary>
public sealed class ClosestRayHit2D : IPhysxRayCastCallback2D
{
    /// <param name="length">The ray length used to turn fractions into distances (usually the
    /// distance from <c>from</c> to <c>to</c>, passed as computed by the caller).</param>
    /// <param name="filter">Accepts a fixture (null accepts all).</param>
    public ClosestRayHit2D(float length, Func<IPhysxFixture2D, bool>? filter = null)
    {
        Length = length;
        Filter = filter;
    }

    /// <summary>Ray length used to convert hit fractions into <see cref="Distance"/> (world units).
    /// Change it between casts of different lengths so distances stay comparable.</summary>
    public float Length { get; set; }

    /// <summary>Accepts a fixture; rejected fixtures are ignored (the ray is not clipped). Null accepts all.</summary>
    public Func<IPhysxFixture2D, bool>? Filter { get; set; }

    /// <summary>True when an accepted hit was kept.</summary>
    public bool Found { get; private set; }

    /// <summary>The fixture of the kept hit (null when none).</summary>
    public IPhysxFixture2D? Fixture { get; private set; }

    /// <summary>World-space hit point of the kept hit.</summary>
    public Vector2 Point { get; private set; }

    /// <summary>Unit surface normal at the kept hit (world space, as reported by the engine).</summary>
    public Vector2 Normal { get; private set; }

    /// <summary>Fraction (0..1) along the ray that produced the kept hit.</summary>
    public float Fraction { get; private set; }
    /// <summary><c>Fraction * Length</c> of the kept hit; +∞ when none.</summary>
    public float Distance { get; private set; } = float.PositiveInfinity;

    /// <summary>Clears the kept hit (<see cref="Found"/> false, <see cref="Distance"/> +∞); keeps
    /// <see cref="Length"/> and <see cref="Filter"/>.</summary>
    public void Reset()
    {
        Found = false;
        Fixture = null;
        Point = default;
        Normal = default;
        Fraction = 0;
        Distance = float.PositiveInfinity;
    }

    /// <inheritdoc/>
    /// <remarks>Returns -1 for a filtered-out fixture, otherwise <paramref name="fraction"/> (clips the ray).</remarks>
    public float OnHit(IPhysxFixture2D fixture, Vector2 point, Vector2 normal, float fraction)
    {
        if (Filter is not null && !Filter(fixture)) return -1;
        var dist = fraction * Length;
        if (dist < Distance)
        {
            Distance = dist;
            Found = true;
            Fixture = fixture;
            Point = point;
            Normal = normal;
            Fraction = fraction;
        }
        return fraction;
    }
}

/// <summary>Physics layer. Ray-cast conveniences on <see cref="IPhysxWorldEngine2D"/>.</summary>
public static class RayCastExtensions2D
{
    /// <summary>Casts from <paramref name="from"/> to <paramref name="to"/> and returns the closest hit
    /// accepted by <paramref name="filter"/> (check <see cref="ClosestRayHit2D.Found"/>). Distances use
    /// <paramref name="length"/>, or <c>Vector2.Distance(from, to)</c> when null. Allocates one
    /// <see cref="ClosestRayHit2D"/> per call; reuse an instance (and <see cref="ClosestRayHit2D.Reset"/>)
    /// in hot paths.</summary>
    /// <param name="world">The world to cast in.</param>
    /// <param name="from">Ray start (world).</param>
    /// <param name="to">Ray end (world).</param>
    /// <param name="filter">Accepts a fixture (null accepts all).</param>
    /// <param name="length">Distance scale for fractions; pass a precomputed value to match other code bit for bit.</param>
    public static ClosestRayHit2D RayCastClosest(this IPhysxWorldEngine2D world, Vector2 from, Vector2 to,
                                                 Func<IPhysxFixture2D, bool>? filter = null, float? length = null)
    {
        var hit = new ClosestRayHit2D(length ?? Vector2.Distance(from, to), filter);
        world.RayCast(from, to, hit);
        return hit;
    }
}
