/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.Combat;

/// <summary>
/// Defines an AoE shape for combat sweeps.
/// Query semantics are controlled by <see cref="Space"/>:
/// planar spaces interpret cone/line in 2D, while <see cref="SweepSpace.ThreeD"/>
/// uses full 3D volume checks.
///
/// 2D analogue: <see cref="Altruist.Gaming.Combat.TwoD.SweepQuery2D"/>.
/// </summary>
/// <remarks>
/// <para>
/// Consumed by <see cref="ICombatService.Sweep"/>. Pick a factory by how your game measures direction:
/// <list type="bullet">
/// <item>Yaw factories (<c>Vector3</c>/<c>Position3D</c> origin + yaw): ground-plane queries for 3D games
/// (+Y up). They set <see cref="SweepSpace.PlanarXZ"/> and take the canonical Altruist yaw, <c>Atan2(dx, dz)</c>
/// (0 = +Z, π/2 = +X), so you can pass an entity's facing directly. Prefer these.</item>
/// <item>Float + planar <c>direction</c> factories: <see cref="SweepSpace.PlanarXY"/> with a math angle
/// <c>Atan2(dy, dx)</c> in radians (0 = +X, counter-clockwise toward +Y).</item>
/// <item>Float + direction-vector factories: full 3D volume (<see cref="SweepSpace.ThreeD"/>), for aiming up/down.</item>
/// </list>
/// Use <c>with { ... }</c> to set <see cref="MaxTargets"/>, <see cref="Filter"/>, <see cref="Width"/> or to change
/// <see cref="Space"/> after construction.
/// </para>
/// <para>Units are world units; <see cref="Angle"/> is a FULL opening angle in degrees while the yaw factories take a
/// HALF angle in radians.</para>
/// </remarks>
/// <example>
/// <code>
/// var aoe   = SweepQuery3D.Sphere(pos, radius: 4f) with { MaxTargets = 10 };
/// var cleave = SweepQuery3D.Cone(pos, yaw, range: 3f, halfAngleRadians: MathF.PI / 3)
///              with { Filter = e =&gt; e is Monster };
/// var beam  = SweepQuery3D.Line(pos, yaw, length: 12f, halfWidth: 0.5f);
/// var aimed = SweepQuery3D.Cone(x, y, z, range: 8f, dirX: fx, dirY: fy, dirZ: fz, angle: 30f); // 3D volume
/// </code>
/// </example>
public record SweepQuery3D
{
    /// <summary>Shape to test.</summary>
    public SweepType Type { get; init; }
    /// <summary>Plane/volume the shape is evaluated in. Defaults to <see cref="SweepSpace.PlanarXY"/>; the yaw
    /// factories set <see cref="SweepSpace.PlanarXZ"/> and the direction-vector factories <see cref="SweepSpace.ThreeD"/>.</summary>
    public SweepSpace Space { get; init; } = SweepSpace.PlanarXY;
    /// <summary>Origin X (cone apex / line start / sphere center).</summary>
    public float CenterX { get; init; }
    /// <summary>Origin Y.</summary>
    public float CenterY { get; init; }
    /// <summary>Origin Z.</summary>
    public float CenterZ { get; init; }
    /// <summary>Sphere radius, cone length or line length, in world units.</summary>
    public float Range { get; init; }
    /// <summary>Cone only: FULL opening angle in degrees (a target is inside when within <c>Angle / 2</c> of the direction).</summary>
    public float Angle { get; init; }       // Cone only (degrees)
    /// <summary>Planar cone/line direction as a math angle in radians, <c>Atan2(second axis, first axis)</c> of the
    /// plane (XY: atan2(y, x); XZ: atan2(z, x); YZ: atan2(z, y)). Ignored in <see cref="SweepSpace.ThreeD"/>.</summary>
    public float Direction { get; init; }   // Planar cone/line direction (radians)
    /// <summary>3D cone/line direction X (used only in <see cref="SweepSpace.ThreeD"/>; need not be normalized;
    /// a zero vector matches nothing).</summary>
    public float DirectionX { get; init; }  // 3D cone/line direction vector
    /// <summary>3D direction Y component (see <see cref="DirectionX"/>).</summary>
    public float DirectionY { get; init; }
    /// <summary>3D direction Z component (see <see cref="DirectionX"/>).</summary>
    public float DirectionZ { get; init; }
    /// <summary>Line only: half-width (max perpendicular distance from the line axis). When 0 or negative a default
    /// half-width of 200 world units is used, so always set it for small-scale worlds.</summary>
    public float Width { get; init; }       // Line thickness / half-width
    /// <summary>Maximum number of entities hit; 0 = unlimited. Targets are taken in world snapshot order, not nearest-first.</summary>
    public int MaxTargets { get; init; }    // 0 = unlimited
    /// <summary>Optional predicate; entities for which it returns false are ignored (e.g. teams, friendly fire).</summary>
    public Func<ICombatEntity, bool>? Filter { get; init; }

    /// <summary>Circle of <paramref name="radius"/> in the default <see cref="SweepSpace.PlanarXY"/> plane (Z ignored).
    /// For 3D ground-plane games use <see cref="Sphere(Vector3, float)"/>, or <c>with { Space = SweepSpace.ThreeD }</c>
    /// for a true sphere.</summary>
    /// <param name="x">Center X.</param>
    /// <param name="y">Center Y.</param>
    /// <param name="z">Center Z.</param>
    /// <param name="radius">Radius in world units.</param>
    public static SweepQuery3D Sphere(float x, float y, float z, float radius)
        => new() { Type = SweepType.Sphere, CenterX = x, CenterY = y, CenterZ = z, Range = radius };

    /// <summary>Planar cone in <see cref="SweepSpace.PlanarXY"/>. Targets closer than 0.001 units to the apex are excluded.
    /// For yaw-based 3D games use <see cref="Cone(Vector3, float, float, float)"/>.</summary>
    /// <param name="x">Apex X.</param>
    /// <param name="y">Apex Y.</param>
    /// <param name="z">Apex Z.</param>
    /// <param name="range">Cone length.</param>
    /// <param name="direction">Math angle in radians, <c>Atan2(dy, dx)</c>.</param>
    /// <param name="angle">FULL opening angle in degrees.</param>
    public static SweepQuery3D Cone(float x, float y, float z, float range, float direction, float angle)
        => new() { Type = SweepType.Cone, CenterX = x, CenterY = y, CenterZ = z, Range = range, Direction = direction, Angle = angle };

    /// <summary>True 3D cone (<see cref="SweepSpace.ThreeD"/>) around a direction vector, e.g. aiming up or down.
    /// Targets at the apex count as inside.</summary>
    /// <param name="x">Apex X.</param>
    /// <param name="y">Apex Y.</param>
    /// <param name="z">Apex Z.</param>
    /// <param name="range">Cone length (3D distance).</param>
    /// <param name="dirX">Direction X (normalized internally).</param>
    /// <param name="dirY">Direction Y.</param>
    /// <param name="dirZ">Direction Z.</param>
    /// <param name="angle">FULL opening angle in degrees.</param>
    public static SweepQuery3D Cone(float x, float y, float z, float range, float dirX, float dirY, float dirZ, float angle)
        => new()
        {
            Type = SweepType.Cone,
            Space = SweepSpace.ThreeD,
            CenterX = x,
            CenterY = y,
            CenterZ = z,
            Range = range,
            DirectionX = dirX,
            DirectionY = dirY,
            DirectionZ = dirZ,
            Angle = angle,
        };

    /// <summary>Planar line in <see cref="SweepSpace.PlanarXY"/>. <see cref="Width"/> is left at 0, which means the
    /// 200-unit default half-width; set it with <c>with { Width = ... }</c> or use
    /// <see cref="Line(Vector3, float, float, float)"/>.</summary>
    /// <param name="x">Start X.</param>
    /// <param name="y">Start Y.</param>
    /// <param name="z">Start Z.</param>
    /// <param name="length">Line length.</param>
    /// <param name="direction">Math angle in radians, <c>Atan2(dy, dx)</c>.</param>
    public static SweepQuery3D Line(float x, float y, float z, float length, float direction)
        => new() { Type = SweepType.Line, CenterX = x, CenterY = y, CenterZ = z, Range = length, Direction = direction };

    /// <summary>3D line/cylinder (<see cref="SweepSpace.ThreeD"/>) along a direction vector. <see cref="Width"/>
    /// (radius) is left at 0, which means the 200-unit default; set it with <c>with { Width = ... }</c>.</summary>
    /// <param name="x">Start X.</param>
    /// <param name="y">Start Y.</param>
    /// <param name="z">Start Z.</param>
    /// <param name="length">Line length.</param>
    /// <param name="dirX">Direction X (normalized internally; zero vector matches nothing).</param>
    /// <param name="dirY">Direction Y.</param>
    /// <param name="dirZ">Direction Z.</param>
    public static SweepQuery3D Line(float x, float y, float z, float length, float dirX, float dirY, float dirZ)
        => new()
        {
            Type = SweepType.Line,
            Space = SweepSpace.ThreeD,
            CenterX = x,
            CenterY = y,
            CenterZ = z,
            Range = length,
            DirectionX = dirX,
            DirectionY = dirY,
            DirectionZ = dirZ,
        };

    // ---------- Yaw-aware ergonomic factories (XZ-plane) ----------
    //
    // Yaw convention: callers pass the canonical Altruist yaw
    // (Yaw3D.FromDirection = Atan2(dx, dz)). The factory converts to the
    // math-angle convention (Atan2(dz, dx) = π/2 − yaw) that
    // CombatService's planar cone / line tests use internally.

    private static float YawToMathAngle(float yaw) => MathF.PI * 0.5f - yaw;

    /// <summary>Ground-plane (<see cref="SweepSpace.PlanarXZ"/>) cone facing <paramref name="yaw"/>; height (Y) is ignored.
    /// Preferred cone factory for 3D games.</summary>
    /// <param name="origin">Apex position.</param>
    /// <param name="yaw">Facing in radians, canonical Altruist yaw <c>Atan2(dx, dz)</c> (0 = +Z).</param>
    /// <param name="range">Cone length.</param>
    /// <param name="halfAngleRadians">HALF opening angle in radians (stored as a full angle in degrees).</param>
    public static SweepQuery3D Cone(Vector3 origin, float yaw, float range, float halfAngleRadians)
    {
        var fullAngleDegrees = Altruist.Numerics.Angle.ToDegrees(halfAngleRadians) * 2f;
        return Cone(origin.X, origin.Y, origin.Z, range,
                    direction: YawToMathAngle(yaw), angle: fullAngleDegrees)
            with { Space = SweepSpace.PlanarXZ };
    }

    /// <summary><see cref="Position3D"/> overload of <see cref="Cone(Vector3, float, float, float)"/>.</summary>
    /// <param name="origin">Apex position.</param>
    /// <param name="yaw">Facing yaw in radians, <c>Atan2(dx, dz)</c>.</param>
    /// <param name="range">Cone length.</param>
    /// <param name="halfAngleRadians">HALF opening angle in radians.</param>
    public static SweepQuery3D Cone(Position3D origin, float yaw, float range, float halfAngleRadians)
        => Cone(origin.ToVector3(), yaw, range, halfAngleRadians);

    /// <summary>Ground-plane (<see cref="SweepSpace.PlanarXZ"/>) line facing <paramref name="yaw"/>; height (Y) is ignored.
    /// Preferred line factory for 3D games.</summary>
    /// <param name="origin">Start position.</param>
    /// <param name="yaw">Facing in radians, canonical Altruist yaw <c>Atan2(dx, dz)</c> (0 = +Z).</param>
    /// <param name="length">Line length.</param>
    /// <param name="halfWidth">Max perpendicular distance from the axis (0 falls back to 200).</param>
    public static SweepQuery3D Line(Vector3 origin, float yaw, float length, float halfWidth)
        => Line(origin.X, origin.Y, origin.Z, length, direction: YawToMathAngle(yaw))
            with { Space = SweepSpace.PlanarXZ, Width = halfWidth };

    /// <summary><see cref="Position3D"/> overload of <see cref="Line(Vector3, float, float, float)"/>.</summary>
    /// <param name="origin">Start position.</param>
    /// <param name="yaw">Facing yaw in radians, <c>Atan2(dx, dz)</c>.</param>
    /// <param name="length">Line length.</param>
    /// <param name="halfWidth">Max perpendicular distance from the axis.</param>
    public static SweepQuery3D Line(Position3D origin, float yaw, float length, float halfWidth)
        => Line(origin.ToVector3(), yaw, length, halfWidth);

    /// <summary>Ground-plane circle (<see cref="SweepSpace.PlanarXZ"/>) around <paramref name="origin"/>; height (Y) is
    /// ignored (but see <see cref="SweepSpace.PlanarXZ"/> about the broadphase). Preferred AoE factory for 3D games;
    /// use <c>with { Space = SweepSpace.ThreeD }</c> for a true sphere.</summary>
    /// <param name="origin">Center.</param>
    /// <param name="radius">Radius in world units.</param>
    public static SweepQuery3D Sphere(Vector3 origin, float radius)
        => Sphere(origin.X, origin.Y, origin.Z, radius)
            with { Space = SweepSpace.PlanarXZ };

    /// <summary><see cref="Position3D"/> overload of <see cref="Sphere(Vector3, float)"/>.</summary>
    /// <param name="origin">Center.</param>
    /// <param name="radius">Radius in world units.</param>
    public static SweepQuery3D Sphere(Position3D origin, float radius)
        => Sphere(origin.ToVector3(), radius);
}

/// <summary>Which coordinates a <see cref="SweepQuery3D"/> is evaluated in.</summary>
public enum SweepSpace
{
    /// <summary>2D test on X/Y, Z ignored. Default for the float factories; matches XY-plane (2D-style) worlds.</summary>
    PlanarXY = 0,
    /// <summary>2D test on the X/Z ground plane, Y (height) ignored; the usual choice for +Y-up 3D games and what the
    /// yaw factories use. Note: sphere queries over more than 50 world objects go through a 3D spatial-hash
    /// broadphase that also bounds Y by the radius, so very tall height differences can be missed.</summary>
    PlanarXZ = 1,
    /// <summary>2D test on Y/Z, X ignored.</summary>
    PlanarYZ = 2,
    /// <summary>Full 3D volume test (sphere, cone around a direction vector, cylinder-like line).</summary>
    ThreeD = 3,
}
