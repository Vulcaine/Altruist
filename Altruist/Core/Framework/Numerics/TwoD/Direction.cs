/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"Which way from A to B?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Direction"/>. Normalized direction
/// vectors with safe coincident-point handling (returns
/// <see cref="Vector2.Zero"/> instead of NaN). Yaw convention mirrors 3D:
/// <c>TowardAngle(0)</c> faces +Y (forward), <c>TowardAngle(π/2)</c> faces +X.</summary>
public static class Direction2D
{
    public static Vector2 Between(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return Vector2.Zero;
        return delta / MathF.Sqrt(lenSq);
    }
    public static Vector2 Between(Position2D from, Position2D to) => Between(from.ToFloatVector2(), to.ToFloatVector2());

    /// <summary>Intent-named alias of <see cref="Between(Vector2,Vector2)"/>.</summary>
    public static Vector2 Toward(Vector2 from, Vector2 to) => Between(from, to);
    public static Vector2 Toward(Position2D from, Position2D to) => Between(from, to);

    /// <summary>Unit vector at <paramref name="rotationRadians"/>. Same shape
    /// as <see cref="Yaw2D.ToDirection(float)"/>, exposed on <c>Direction2D</c>
    /// for ergonomics at "step in this angle" sites.</summary>
    public static Vector2 TowardAngle(float rotationRadians)
        => new(MathF.Sin(rotationRadians), MathF.Cos(rotationRadians));

    public static (Vector2 direction, float distance) WithDistance(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return (Vector2.Zero, 0f);
        var len = MathF.Sqrt(lenSq);
        return (delta / len, len);
    }
    public static (Vector2 direction, float distance) WithDistance(Position2D from, Position2D to)
        => WithDistance(from.ToFloatVector2(), to.ToFloatVector2());

    /// <summary>90° CCW rotation: (x, y) → (-y, x).</summary>
    public static Vector2 Perpendicular(Vector2 dir) => new(-dir.Y, dir.X);
}
