/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Defines an AoE shape for combat sweeps.
/// Query semantics are controlled by <see cref="Space"/>:
/// planar spaces interpret cone/line in 2D, while <see cref="SweepSpace.ThreeD"/>
/// uses full 3D volume checks.
/// </summary>
public record SweepQuery
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

    public static SweepQuery Sphere(float x, float y, float z, float radius)
        => new() { Type = SweepType.Sphere, CenterX = x, CenterY = y, CenterZ = z, Range = radius };

    public static SweepQuery Cone(float x, float y, float z, float range, float direction, float angle)
        => new() { Type = SweepType.Cone, CenterX = x, CenterY = y, CenterZ = z, Range = range, Direction = direction, Angle = angle };

    public static SweepQuery Cone(float x, float y, float z, float range, float dirX, float dirY, float dirZ, float angle)
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

    public static SweepQuery Line(float x, float y, float z, float length, float direction)
        => new() { Type = SweepType.Line, CenterX = x, CenterY = y, CenterZ = z, Range = length, Direction = direction };

    public static SweepQuery Line(float x, float y, float z, float length, float dirX, float dirY, float dirZ)
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
}

public enum SweepSpace
{
    PlanarXY = 0,
    PlanarXZ = 1,
    PlanarYZ = 2,
    ThreeD = 3,
}
