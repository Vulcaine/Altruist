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
/// over all of them; <see cref="Reset"/> to reuse it.</summary>
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

    public float Length { get; set; }
    public Func<IPhysxFixture2D, bool>? Filter { get; set; }

    public bool Found { get; private set; }
    public IPhysxFixture2D? Fixture { get; private set; }
    public Vector2 Point { get; private set; }
    public Vector2 Normal { get; private set; }
    public float Fraction { get; private set; }
    /// <summary><c>Fraction * Length</c> of the kept hit; +∞ when none.</summary>
    public float Distance { get; private set; } = float.PositiveInfinity;

    public void Reset()
    {
        Found = false;
        Fixture = null;
        Point = default;
        Normal = default;
        Fraction = 0;
        Distance = float.PositiveInfinity;
    }

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
    /// <paramref name="length"/>, or <c>Vector2.Distance(from, to)</c> when null.</summary>
    public static ClosestRayHit2D RayCastClosest(this IPhysxWorldEngine2D world, Vector2 from, Vector2 to,
                                                 Func<IPhysxFixture2D, bool>? filter = null, float? length = null)
    {
        var hit = new ClosestRayHit2D(length ?? Vector2.Distance(from, to), filter);
        world.RayCast(from, to, hit);
        return hit;
    }
}
