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
public record SweepQuery2D
{
    public SweepType Type { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float Range { get; init; }
    public float Angle { get; init; }       // Cone only (degrees)
    public float Direction { get; init; }   // Cone/line direction (radians, math-angle convention)
    public float Width { get; init; }       // Line thickness / half-width
    public int MaxTargets { get; init; }    // 0 = unlimited
    public Func<ICombatEntity, bool>? Filter { get; init; }

    // Yaw convention: callers pass canonical Altruist rotation (Atan2(dx, dy)).
    // Internal Direction stored as math-angle (Atan2(dy, dx) = π/2 − rotation)
    // for consistency with the 3D record's PlanarXZ convention.
    private static float RotationToMathAngle(float rotation) => MathF.PI * 0.5f - rotation;

    public static SweepQuery2D Sphere(Vector2 origin, float radius)
        => new() { Type = SweepType.Sphere, CenterX = origin.X, CenterY = origin.Y, Range = radius };

    public static SweepQuery2D Sphere(Position2D origin, float radius)
        => Sphere(origin.ToFloatVector2(), radius);

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

    public static SweepQuery2D Cone(Position2D origin, float rotation, float range, float halfAngleRadians)
        => Cone(origin.ToFloatVector2(), rotation, range, halfAngleRadians);

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

    public static SweepQuery2D Line(Position2D origin, float rotation, float length, float halfWidth)
        => Line(origin.ToFloatVector2(), rotation, length, halfWidth);
}
