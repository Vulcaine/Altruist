/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"Which way from A to B?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Direction3D"/>. Normalized direction
/// vectors with safe coincident-point handling (returns
/// <see cref="Vector2.Zero"/> instead of NaN). Yaw convention mirrors 3D:
/// <c>TowardAngle(0)</c> faces +Y (forward), <c>TowardAngle(π/2)</c> faces +X.</summary>
public static class Direction2D
{
    /// <summary>Unit vector from <paramref name="from"/> to <paramref name="to"/>;
    /// <see cref="Vector2.Zero"/> when the points coincide (squared distance below 1e-12). For an
    /// already-computed vector use <see cref="VectorMath2D.NormalizeOrZero(Vector2)"/>.</summary>
    public static Vector2 Between(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return Vector2.Zero;
        return delta / MathF.Sqrt(lenSq);
    }
    /// <summary><see cref="Position2D"/> overload of <see cref="Between(Vector2,Vector2)"/>.</summary>
    public static Vector2 Between(Position2D from, Position2D to) => Between(from.ToFloatVector2(), to.ToFloatVector2());

    /// <summary>Intent-named alias of <see cref="Between(Vector2,Vector2)"/>.</summary>
    public static Vector2 Toward(Vector2 from, Vector2 to) => Between(from, to);
    /// <summary><see cref="Position2D"/> overload of <see cref="Toward(Vector2,Vector2)"/>.</summary>
    public static Vector2 Toward(Position2D from, Position2D to) => Between(from, to);

    /// <summary>Unit vector at <paramref name="rotationRadians"/>. Same shape
    /// as <see cref="Yaw2D.ToDirection(float)"/>, exposed on <c>Direction2D</c>
    /// for ergonomics at "step in this angle" sites.</summary>
    public static Vector2 TowardAngle(float rotationRadians)
        => new(DeterministicMath.Sin(rotationRadians), DeterministicMath.Cos(rotationRadians));

    /// <summary>Both the unit direction and the distance from <paramref name="from"/> to
    /// <paramref name="to"/> in one square root; <c>(Vector2.Zero, 0)</c> when the points coincide. Use it
    /// when a caller needs an arrival test and a per-tick step together.</summary>
    public static (Vector2 direction, float distance) WithDistance(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return (Vector2.Zero, 0f);
        var len = MathF.Sqrt(lenSq);
        return (delta / len, len);
    }
    /// <summary><see cref="Position2D"/> overload of <see cref="WithDistance(Vector2,Vector2)"/>.</summary>
    public static (Vector2 direction, float distance) WithDistance(Position2D from, Position2D to)
        => WithDistance(from.ToFloatVector2(), to.ToFloatVector2());

    /// <summary>90° CCW rotation: (x, y) → (-y, x).</summary>
    public static Vector2 Perpendicular(Vector2 dir) => new(-dir.Y, dir.X);

    /// <summary>90° clockwise rotation: (x, y) → (y, -x). For a surface normal pointing out of
    /// a floor (+Y) this is the tangent pointing +X.</summary>
    public static Vector2 PerpendicularClockwise(Vector2 dir) => new(dir.Y, -dir.X);

    /// <summary>Unit vector at a polar angle in the standard math convention (0 = +X,
    /// counter-clockwise, the convention of <see cref="Rotation2D"/> and physics bodies):
    /// <c>(DeterministicMath.Cos(radians), DeterministicMath.Sin(radians))</c>. Not the yaw convention of
    /// <see cref="TowardAngle"/> (0 = +Y, clockwise).</summary>
    public static Vector2 FromPolar(float radians) => new(DeterministicMath.Cos(radians), DeterministicMath.Sin(radians));

    /// <summary><see cref="FromPolar"/> in degrees:
    /// <c>(DeterministicMath.Cos(degrees * (π / 180)), DeterministicMath.Sin(degrees * (π / 180)))</c>.</summary>
    public static Vector2 FromPolarDegrees(float degrees) => FromPolar(Angle.ToRadians(degrees));

    /// <summary>Limits the Y component (elevation) of the unit vector <paramref name="unit"/> to
    /// [<paramref name="minY"/>, <paramref name="maxY"/>] and rebuilds X on the same side so the
    /// result stays unit length: when Y is clamped,
    /// <c>x = MathF.Sign(unit.X) * MathF.Sqrt(1 - y * y)</c>. Y above <paramref name="maxY"/> is
    /// checked first. A vertical input (X = 0) stays vertical in X (0).</summary>
    public static Vector2 ClampElevation(Vector2 unit, float minY, float maxY)
    {
        var ux = unit.X;
        var uy = unit.Y;
        if (uy > maxY)
        {
            uy = maxY;
            ux = MathF.Sign(ux) * MathF.Sqrt(1 - uy * uy);
        }
        else if (uy < minY)
        {
            uy = minY;
            ux = MathF.Sign(ux) * MathF.Sqrt(1 - uy * uy);
        }
        return new Vector2(ux, uy);
    }
}
