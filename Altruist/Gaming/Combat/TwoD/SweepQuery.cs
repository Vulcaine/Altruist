/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.Combat.TwoD;

/// <summary>
/// 2D mirror of <see cref="Altruist.Gaming.Combat.SweepQuery3D"/>. XY-plane;
/// no <c>SweepSpace</c> enum (2D is implicitly XY).
/// </summary>
/// <remarks>
/// Use this to describe area shapes for 2D (XY-plane) games. Note that <see cref="ICombatService.Sweep"/> currently
/// accepts only <see cref="SweepQuery3D"/> and no framework service consumes <see cref="SweepQuery2D"/>; to run a 2D
/// shape through the built-in service, build the equivalent <see cref="SweepQuery3D"/> in
/// <see cref="SweepSpace.PlanarXY"/> (same <see cref="Direction"/>/<see cref="Angle"/>/<see cref="Width"/> semantics),
/// or evaluate the query in your own 2D code. Field meanings mirror <see cref="SweepQuery3D"/>.
/// </remarks>
public record SweepQuery2D
{
    /// <summary>Shape to test.</summary>
    public SweepType Type { get; init; }
    /// <summary>Origin X (cone apex / line start / circle center).</summary>
    public float CenterX { get; init; }
    /// <summary>Origin Y.</summary>
    public float CenterY { get; init; }
    /// <summary>Circle radius, cone length or line length, in world units.</summary>
    public float Range { get; init; }
    /// <summary>Cone only: FULL opening angle in degrees.</summary>
    public float Angle { get; init; }       // Cone only (degrees)
    /// <summary>Cone/line direction as a math angle in radians, <c>Atan2(dy, dx)</c> (0 = +X). The factories convert
    /// from the canonical Altruist 2D rotation for you.</summary>
    public float Direction { get; init; }   // Cone/line direction (radians, math-angle convention)
    /// <summary>Line only: half-width (max perpendicular distance from the axis).</summary>
    public float Width { get; init; }       // Line thickness / half-width
    /// <summary>Maximum number of entities hit; 0 = unlimited.</summary>
    public int MaxTargets { get; init; }    // 0 = unlimited
    /// <summary>Optional predicate; entities for which it returns false are ignored.</summary>
    public Func<ICombatEntity, bool>? Filter { get; init; }

    // Yaw convention: callers pass canonical Altruist rotation (Atan2(dx, dy)).
    // Internal Direction stored as math-angle (Atan2(dy, dx) = π/2 − rotation)
    // for consistency with the 3D record's PlanarXZ convention.
    private static float RotationToMathAngle(float rotation) => MathF.PI * 0.5f - rotation;

    /// <summary>Circle of <paramref name="radius"/> around <paramref name="origin"/>.</summary>
    /// <param name="origin">Center.</param>
    /// <param name="radius">Radius in world units.</param>
    public static SweepQuery2D Sphere(Vector2 origin, float radius)
        => new() { Type = SweepType.Sphere, CenterX = origin.X, CenterY = origin.Y, Range = radius };

    /// <summary><see cref="Position2D"/> overload of <see cref="Sphere(Vector2, float)"/>.</summary>
    /// <param name="origin">Center.</param>
    /// <param name="radius">Radius in world units.</param>
    public static SweepQuery2D Sphere(Position2D origin, float radius)
        => Sphere(origin.ToFloatVector2(), radius);

    /// <summary>Cone (wedge) facing <paramref name="rotation"/>.</summary>
    /// <param name="origin">Apex.</param>
    /// <param name="rotation">Facing in radians, canonical Altruist 2D rotation <c>Atan2(dx, dy)</c> (0 = +Y).</param>
    /// <param name="range">Cone length.</param>
    /// <param name="halfAngleRadians">HALF opening angle in radians (stored as a full angle in degrees).</param>
    public static SweepQuery2D Cone(Vector2 origin, float rotation, float range, float halfAngleRadians)
    {
        var fullAngleDegrees = Altruist.Numerics.Angle.ToDegrees(halfAngleRadians) * 2f;
        return new()
        {
            Type = SweepType.Cone,
            CenterX = origin.X,
            CenterY = origin.Y,
            Range = range,
            Direction = RotationToMathAngle(rotation),
            Angle = fullAngleDegrees,
        };
    }

    /// <summary><see cref="Position2D"/> overload of <see cref="Cone(Vector2, float, float, float)"/>.</summary>
    /// <param name="origin">Apex.</param>
    /// <param name="rotation">Facing rotation in radians, <c>Atan2(dx, dy)</c>.</param>
    /// <param name="range">Cone length.</param>
    /// <param name="halfAngleRadians">HALF opening angle in radians.</param>
    public static SweepQuery2D Cone(Position2D origin, float rotation, float range, float halfAngleRadians)
        => Cone(origin.ToFloatVector2(), rotation, range, halfAngleRadians);

    /// <summary>Line (strip) from <paramref name="origin"/> facing <paramref name="rotation"/>.</summary>
    /// <param name="origin">Start.</param>
    /// <param name="rotation">Facing in radians, canonical Altruist 2D rotation <c>Atan2(dx, dy)</c> (0 = +Y).</param>
    /// <param name="length">Line length.</param>
    /// <param name="halfWidth">Max perpendicular distance from the axis.</param>
    public static SweepQuery2D Line(Vector2 origin, float rotation, float length, float halfWidth)
        => new()
        {
            Type = SweepType.Line,
            CenterX = origin.X,
            CenterY = origin.Y,
            Range = length,
            Direction = RotationToMathAngle(rotation),
            Width = halfWidth,
        };

    /// <summary><see cref="Position2D"/> overload of <see cref="Line(Vector2, float, float, float)"/>.</summary>
    /// <param name="origin">Start.</param>
    /// <param name="rotation">Facing rotation in radians, <c>Atan2(dx, dy)</c>.</param>
    /// <param name="length">Line length.</param>
    /// <param name="halfWidth">Max perpendicular distance from the axis.</param>
    public static SweepQuery2D Line(Position2D origin, float rotation, float length, float halfWidth)
        => Line(origin.ToFloatVector2(), rotation, length, halfWidth);
}
