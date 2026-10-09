/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>Stateless spatial predicates — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.SpatialQueries3D"/>.
/// Rotation uses the Altruist 2D convention (<c>Atan2(dx, dy)</c>).</summary>
public static class SpatialQueries2D
{
    /// <summary>Is <paramref name="to"/> within <paramref name="range"/> of <paramref name="from"/>?</summary>
    public static bool IsInRange(Vector2 from, Vector2 to, float range)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return dx * dx + dy * dy <= range * range;
    }
    /// <summary><see cref="Position2D"/> overload of <see cref="IsInRange(Vector2,Vector2,float)"/>.</summary>
    public static bool IsInRange(Position2D from, Position2D to, float range)
        => IsInRange(from.ToFloatVector2(), to.ToFloatVector2(), range);

    /// <summary>Cone test: target is in-cone iff it's within <paramref name="range"/> of
    /// <paramref name="origin"/> and the angle between (origin→target) and the cone axis
    /// (<paramref name="rotationRadians"/>) is ≤ <paramref name="halfAngleRadians"/>.</summary>
    public static bool IsInCone(Vector2 origin, float rotationRadians, float halfAngleRadians, float range, Vector2 target)
    {
        var dx = target.X - origin.X;
        var dy = target.Y - origin.Y;
        var distSq = dx * dx + dy * dy;
        if (distSq > range * range) return false;
        if (distSq < 1e-6f) return true;

        var targetYaw = MathF.Atan2(dx, dy);
        var diff = Angle.ShortestDifference(rotationRadians, targetYaw);
        return MathF.Abs(diff) <= halfAngleRadians;
    }
    /// <summary><see cref="Position2D"/> overload of <see cref="IsInCone(Vector2,float,float,float,Vector2)"/>.</summary>
    public static bool IsInCone(Position2D origin, float rotationRadians, float halfAngleRadians, float range, Position2D target)
        => IsInCone(origin.ToFloatVector2(), rotationRadians, halfAngleRadians, range, target.ToFloatVector2());

    /// <summary>Line/rectangle test: target is on-line iff the projection along the rotation
    /// axis is in [0, length] and the perpendicular distance is ≤ <paramref name="halfWidth"/>.</summary>
    public static bool IsInLine(Vector2 origin, float rotationRadians, float length, float halfWidth, Vector2 target)
    {
        var dx = target.X - origin.X;
        var dy = target.Y - origin.Y;
        var dirX = MathF.Sin(rotationRadians);
        var dirY = MathF.Cos(rotationRadians);

        var along = dx * dirX + dy * dirY;
        if (along < 0f || along > length) return false;

        var perp = MathF.Abs(-dx * dirY + dy * dirX);
        return perp <= halfWidth;
    }
    /// <summary><see cref="Position2D"/> overload of <see cref="IsInLine(Vector2,float,float,float,Vector2)"/>.</summary>
    public static bool IsInLine(Position2D origin, float rotationRadians, float length, float halfWidth, Position2D target)
        => IsInLine(origin.ToFloatVector2(), rotationRadians, length, halfWidth, target.ToFloatVector2());
}
