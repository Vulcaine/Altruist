/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>"How far apart are two points?" — full 3D and XZ-plane variants.
/// Reads naturally at the call site (<c>Distance.Calculate(a, b)</c>).</summary>
public static class Distance
{
    /// <summary>Full 3D distance between two world points.</summary>
    public static float Calculate(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    /// <summary>3D distance squared — use for thresholded comparisons to skip
    /// the square root.</summary>
    public static float Squared(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b);

    /// <summary>XZ-plane distance — Y is ignored. The right answer for
    /// "are these two entities standing near each other on the ground?"</summary>
    public static float Horizontal(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static float HorizontalSquared(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
