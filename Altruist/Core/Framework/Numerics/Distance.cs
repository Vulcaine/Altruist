/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>"How far apart are two points?" — full 3D and XZ-plane variants.
/// Reads naturally at the call site (<c>Distance3D.Between(a, b)</c>).</summary>
public static class Distance3D
{
    /// <summary>Full 3D distance between two world points.</summary>
    public static float Between(Vector3 a, Vector3 b) => Vector3.Distance(a, b);
    public static float Between(Position3D a, Position3D b) => Between(a.ToVector3(), b.ToVector3());

    /// <summary>3D distance squared — use for thresholded comparisons to skip
    /// the square root.</summary>
    public static float Squared(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b);
    public static float Squared(Position3D a, Position3D b) => Squared(a.ToVector3(), b.ToVector3());

    /// <summary>XZ-plane distance — Y is ignored. The right answer for
    /// "are these two entities standing near each other on the ground?"</summary>
    public static float Horizontal(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
    public static float Horizontal(Position3D a, Position3D b) => Horizontal(a.ToVector3(), b.ToVector3());

    public static float HorizontalSquared(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
    public static float HorizontalSquared(Position3D a, Position3D b) => HorizontalSquared(a.ToVector3(), b.ToVector3());
}
