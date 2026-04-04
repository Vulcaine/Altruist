/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Defines an AoE shape for combat sweeps.
/// Use static factory methods: Sphere, Cone, Line.
/// </summary>
public record SweepQuery
{
    public SweepType Type { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public float Range { get; init; }
    public float Angle { get; init; }       // Cone only (degrees)
    public float Direction { get; init; }   // Cone/Line (radians)
    public int MaxTargets { get; init; }    // 0 = unlimited

    public static SweepQuery Sphere(float x, float y, float z, float radius)
        => new() { Type = SweepType.Sphere, CenterX = x, CenterY = y, CenterZ = z, Range = radius };

    public static SweepQuery Cone(float x, float y, float z, float range, float direction, float angle)
        => new() { Type = SweepType.Cone, CenterX = x, CenterY = y, CenterZ = z, Range = range, Direction = direction, Angle = angle };

    public static SweepQuery Line(float x, float y, float z, float length, float direction)
        => new() { Type = SweepType.Line, CenterX = x, CenterY = y, CenterZ = z, Range = length, Direction = direction };
}
