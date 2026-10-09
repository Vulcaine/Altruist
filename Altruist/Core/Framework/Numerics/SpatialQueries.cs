/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.ThreeD.Numerics;

/// <summary>Stateless spatial predicates that any system needs:
/// "is the target in range / in my front cone / on this line?"
/// Yaw uses the Altruist convention (<see cref="Yaw3D"/>: <c>Atan2(dx, dz)</c>, 0 = +Z, π/2 = +X).</summary>
public static class SpatialQueries3D
{
    /// <summary>Is <paramref name="to"/> within <paramref name="range"/> of <paramref name="from"/>?
    /// When <paramref name="xzOnly"/> is true (default), Y is ignored.</summary>
    public static bool IsInRange(Vector3 from, Vector3 to, float range, bool xzOnly = true)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        if (xzOnly) return dx * dx + dz * dz <= range * range;
        var dy = to.Y - from.Y;
        return dx * dx + dy * dy + dz * dz <= range * range;
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="IsInRange(Vector3,Vector3,float,bool)"/>.</summary>
    public static bool IsInRange(Position3D from, Position3D to, float range, bool xzOnly = true)
        => IsInRange(from.ToVector3(), to.ToVector3(), range, xzOnly);

    /// <summary>Cone test in the XZ plane: target is in-cone iff it's within
    /// <paramref name="range"/> of <paramref name="origin"/> and the angle
    /// between (origin→target) and the cone axis (yaw) is ≤ <paramref name="halfAngleRadians"/>.</summary>
    public static bool IsInCone(Vector3 origin, float yaw, float halfAngleRadians, float range, Vector3 target)
    {
        var dx = target.X - origin.X;
        var dz = target.Z - origin.Z;
        var distSq = dx * dx + dz * dz;
        if (distSq > range * range) return false;
        if (distSq < 1e-6f) return true;

        var targetYaw = MathF.Atan2(dx, dz);
        var diff = Angle.ShortestDifference(yaw, targetYaw);
        return MathF.Abs(diff) <= halfAngleRadians;
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="IsInCone(Vector3,float,float,float,Vector3)"/>.</summary>
    public static bool IsInCone(Position3D origin, float yaw, float halfAngleRadians, float range, Position3D target)
        => IsInCone(origin.ToVector3(), yaw, halfAngleRadians, range, target.ToVector3());

    /// <summary>Line/rectangle test in the XZ plane: target is on-line iff
    /// the projection along the yaw axis is in [0, length] and the
    /// perpendicular distance is ≤ <paramref name="halfWidth"/>.</summary>
    public static bool IsInLine(Vector3 origin, float yaw, float length, float halfWidth, Vector3 target)
    {
        var dx = target.X - origin.X;
        var dz = target.Z - origin.Z;
        var dirX = MathF.Sin(yaw);
        var dirZ = MathF.Cos(yaw);

        var along = dx * dirX + dz * dirZ;
        if (along < 0f || along > length) return false;

        // Perpendicular = (-dirZ, dirX) in XZ; signed distance is dot with that.
        var perp = MathF.Abs(-dx * dirZ + dz * dirX);
        return perp <= halfWidth;
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="IsInLine(Vector3,float,float,float,Vector3)"/>.</summary>
    public static bool IsInLine(Position3D origin, float yaw, float length, float halfWidth, Position3D target)
        => IsInLine(origin.ToVector3(), yaw, length, halfWidth, target.ToVector3());
}
