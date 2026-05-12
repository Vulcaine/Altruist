/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.Combat;

/// <summary>Vector-and-yaw fluent factory for <see cref="SweepQuery"/> in
/// the XZ plane — the canonical layout for 3D MMOs where Y is vertical.
/// Wraps the existing <see cref="SweepQuery.Cone(float,float,float,float,float,float)"/>
/// / <see cref="SweepQuery.Line(float,float,float,float,float)"/>
/// / <see cref="SweepQuery.Sphere"/> constructors and sets
/// <see cref="SweepSpace.PlanarXZ"/> automatically. Half-angle radians and
/// half-width meters are the canonical inputs (matches
/// <see cref="SpatialQueries.IsInCone"/> / <see cref="SpatialQueries.IsInLine"/>).
///
/// Yaw convention: callers pass the canonical Altruist yaw
/// (<see cref="Yaw.FromDirection"/> = <c>Atan2(dx, dz)</c>). The factory
/// converts to the math-angle convention (<c>Atan2(dz, dx) = π/2 − yaw</c>)
/// that <see cref="CombatService"/>'s planar cone / line tests use internally.
/// Existing <see cref="SweepQuery.Cone(float,float,float,float,float,float)"/>
/// / <see cref="SweepQuery.Line(float,float,float,float,float)"/> overloads
/// stay around for legacy direct-construction; use this factory for new
/// code.</summary>
public static class SweepQueryFactory
{
    private static float YawToMathAngle(float yaw) => MathF.PI * 0.5f - yaw;

    public static SweepQuery Cone(Vector3 origin, float yaw, float range, float halfAngleRadians)
    {
        var fullAngleDegrees = Angle.ToDegrees(halfAngleRadians) * 2f;
        return SweepQuery.Cone(origin.X, origin.Y, origin.Z, range,
                               direction: YawToMathAngle(yaw), angle: fullAngleDegrees)
            with { Space = SweepSpace.PlanarXZ };
    }

    public static SweepQuery Line(Vector3 origin, float yaw, float length, float halfWidth)
        => SweepQuery.Line(origin.X, origin.Y, origin.Z, length, direction: YawToMathAngle(yaw))
            with { Space = SweepSpace.PlanarXZ, Width = halfWidth };

    public static SweepQuery Sphere(Vector3 origin, float radius)
        => SweepQuery.Sphere(origin.X, origin.Y, origin.Z, radius)
            with { Space = SweepSpace.PlanarXZ };
}
