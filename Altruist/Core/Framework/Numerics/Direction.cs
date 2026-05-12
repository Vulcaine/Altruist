/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>"Which way from A to B?" — normalized direction vectors with
/// safe coincident-point handling (returns <see cref="Vector3.Zero"/>
/// instead of NaN).</summary>
public static class Direction
{
    /// <summary>Full 3D unit vector from <paramref name="from"/> to
    /// <paramref name="to"/>. <see cref="Vector3.Zero"/> when the points
    /// coincide.</summary>
    public static Vector3 Calculate(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return Vector3.Zero;
        return delta / MathF.Sqrt(lenSq);
    }

    /// <summary>XZ-plane direction with Y forced to 0 — for "face the
    /// target" / "step toward the target" patterns where vertical
    /// component would push the body into the ground.</summary>
    public static Vector3 Horizontal(Vector3 from, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6f) return Vector3.Zero;
        var inv = 1f / len;
        return new Vector3(dx * inv, 0f, dz * inv);
    }

    /// <summary>Both 3D direction and 3D distance at once. Common when the
    /// caller needs both for an arrival test + a per-tick step.</summary>
    public static (Vector3 direction, float distance) WithDistance(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        var lenSq = delta.LengthSquared();
        if (lenSq < 1e-12f) return (Vector3.Zero, 0f);
        var len = MathF.Sqrt(lenSq);
        return (delta / len, len);
    }

    /// <summary>90° CCW rotation in the XZ plane (right-hand rule about +Y).
    /// Useful for lateral-offset patterns (knockback recipes, sidestep AI).</summary>
    public static Vector3 PerpendicularXZ(Vector3 dirXZ) => new(dirXZ.Z, 0f, -dirXZ.X);
}
