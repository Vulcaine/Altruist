/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"Which way is this entity facing?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Yaw3D"/>. Single canonical convention:
/// <c>Atan2(dx, dy)</c>, so yaw=0 means facing +Y (matches 3D's "forward = +Z").</summary>
public static class Yaw2D
{
    /// <summary>Extract the angle from a <see cref="Rotation2D"/>. Returns
    /// <see cref="Rotation2D.Radians"/> unchanged, i.e. in <see cref="Rotation2D"/>'s counter-clockwise,
    /// 0 = +X convention, not converted to this class's 0 = +Y facing convention.</summary>
    public static float Calculate(Rotation2D rotation) => rotation.Radians;

    /// <summary>Yaw of the direction (<paramref name="dx"/>, <paramref name="dy"/>): <c>Atan2(dx, dy)</c>
    /// (0 = +Y, π/2 = +X).</summary>
    public static float FromDirection(float dx, float dy) => MathF.Atan2(dx, dy);

    /// <summary>Yaw that points from <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static float FromDirection(Position2D from, Position2D to)
        => MathF.Atan2(to.X - from.X, to.Y - from.Y);

    /// <summary>Yaw of a direction vector (need not be unit length).</summary>
    public static float FromVector(Vector2 dir) => MathF.Atan2(dir.X, dir.Y);

    /// <summary>Unit facing vector for a yaw: <c>(sin yaw, cos yaw)</c> (inverse of
    /// <see cref="FromVector"/>). For the counter-clockwise polar convention use
    /// <see cref="Direction2D.FromPolar"/>.</summary>
    public static Vector2 ToDirection(float rotationRadians)
        => new(MathF.Sin(rotationRadians), MathF.Cos(rotationRadians));
}
