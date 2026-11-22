/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;
using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"Which way is this entity facing?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Yaw3D"/>. Single canonical convention:
/// <c>Atan2(dx, dy)</c>, so yaw=0 means facing +Y (matches 3D's "forward = +Z").</summary>
public static class Yaw2D
{
    /// <summary>The facing yaw of a body rotated by <paramref name="rotation"/>, in this class's convention
    /// (0 = +Y, π/2 = +X, range [-π, π]). A body faces along its local +Y axis (the 2D counterpart of 3D's
    /// local +Z forward), which <see cref="Rotation2D"/> turns counter-clockwise, so the yaw is the negated,
    /// normalized rotation angle: <c>ToDirection(Calculate(r))</c> equals <c>Rotation2D.UpAt(r.Radians)</c>.
    /// Use it to read a physics body's facing (<c>Calculate(Rotation2D.FromRadians(body.RotationZ))</c>);
    /// to turn a yaw back into a body rotation, negate it.</summary>
    public static float Calculate(Rotation2D rotation) => Angle.Normalize(-rotation.Radians);

    /// <summary>Yaw of the direction (<paramref name="dx"/>, <paramref name="dy"/>): <c>Atan2(dx, dy)</c>
    /// (0 = +Y, π/2 = +X).</summary>
    public static float FromDirection(float dx, float dy) => DeterministicMath.Atan2(dx, dy);

    /// <summary>Yaw that points from <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static float FromDirection(Position2D from, Position2D to)
        => DeterministicMath.Atan2(to.X - from.X, to.Y - from.Y);

    /// <summary>Yaw of a direction vector (need not be unit length).</summary>
    public static float FromVector(Vector2 dir) => DeterministicMath.Atan2(dir.X, dir.Y);

    /// <summary>Unit facing vector for a yaw: <c>(sin yaw, cos yaw)</c> (inverse of
    /// <see cref="FromVector"/>). For the counter-clockwise polar convention use
    /// <see cref="Direction2D.FromPolar"/>.</summary>
    public static Vector2 ToDirection(float rotationRadians)
        => new(DeterministicMath.Sin(rotationRadians), DeterministicMath.Cos(rotationRadians));
}
