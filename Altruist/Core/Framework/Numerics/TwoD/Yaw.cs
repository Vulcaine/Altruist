/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"Which way is this entity facing?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Yaw"/>. Single canonical convention:
/// <c>Atan2(dx, dy)</c>, so yaw=0 means facing +Y (matches 3D's "forward = +Z").</summary>
public static class Yaw2D
{
    /// <summary>Extract the angle from a <see cref="Rotation2D"/>.</summary>
    public static float Calculate(Rotation2D rotation) => rotation.Radians;

    public static float FromDirection(float dx, float dy) => MathF.Atan2(dx, dy);

    public static float FromDirection(Position2D from, Position2D to)
        => MathF.Atan2(to.X - from.X, to.Y - from.Y);

    public static float FromVector(Vector2 dir) => MathF.Atan2(dir.X, dir.Y);

    public static Vector2 ToDirection(float rotationRadians)
        => new(MathF.Sin(rotationRadians), MathF.Cos(rotationRadians));
}
