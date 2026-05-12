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
/// 2D analogue: <c>Altruist.Gaming.Combat.TwoD.SweepQuery3D2D</c>.
/// </summary>
public record SweepQuery3D
{
    public SweepType Type { get; init; }
    public SweepSpace Space { get; init; } = SweepSpace.PlanarXY;
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public float Range { get; init; }
    public float Angle { get; init; }       // Cone only (degrees)
    public float Direction { get; init; }   // Planar cone/line direction (radians)
    public float DirectionX { get; init; }  // 3D cone/line direction vector
    public float DirectionY { get; init; }
    public float DirectionZ { get; init; }
    public float Width { get; init; }       // Line thickness / half-width
    public int MaxTargets { get; init; }    // 0 = unlimited
    public Func<ICombatEntity, bool>? Filter { get; init; }

    public static SweepQuery3D Sphere(float x, float y, float z, float radius)
        => new() { Type = SweepType.Sphere, CenterX = x, CenterY = y, CenterZ = z, Range = radius };

    public static SweepQuery3D Cone(float x, float y, float z, float range, float direction, float angle)
        => new() { Type = SweepType.Cone, CenterX = x, CenterY = y, CenterZ = z, Range = range, Direction = direction, Angle = angle };

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

    public static SweepQuery3D Line(float x, float y, float z, float length, float direction)
        => new() { Type = SweepType.Line, CenterX = x, CenterY = y, CenterZ = z, Range = length, Direction = direction };

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

    public static SweepQuery3D Cone(Vector3 origin, float yaw, float range, float halfAngleRadians)
    {
        var fullAngleDegrees = Altruist.Numerics.Angle.ToDegrees(halfAngleRadians) * 2f;
        return Cone(origin.X, origin.Y, origin.Z, range,
                    direction: YawToMathAngle(yaw), angle: fullAngleDegrees)
            with { Space = SweepSpace.PlanarXZ };
    }

    public static SweepQuery3D Cone(Position3D origin, float yaw, float range, float halfAngleRadians)
        => Cone(origin.ToVector3(), yaw, range, halfAngleRadians);

    public static SweepQuery3D Line(Vector3 origin, float yaw, float length, float halfWidth)
        => Line(origin.X, origin.Y, origin.Z, length, direction: YawToMathAngle(yaw))
            with { Space = SweepSpace.PlanarXZ, Width = halfWidth };

    public static SweepQuery3D Line(Position3D origin, float yaw, float length, float halfWidth)
        => Line(origin.ToVector3(), yaw, length, halfWidth);

    public static SweepQuery3D Sphere(Vector3 origin, float radius)
        => Sphere(origin.X, origin.Y, origin.Z, radius)
            with { Space = SweepSpace.PlanarXZ };

    public static SweepQuery3D Sphere(Position3D origin, float radius)
        => Sphere(origin.ToVector3(), radius);
}

public enum SweepSpace
{
    PlanarXY = 0,
    PlanarXZ = 1,
    PlanarYZ = 2,
    ThreeD = 3,
}
