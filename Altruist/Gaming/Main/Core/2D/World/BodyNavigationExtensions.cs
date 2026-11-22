/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD;

/// <summary>Body verbs that need <see cref="IWorldObject2D"/> — 2D mirror of
/// <see cref="Altruist.Gaming.ThreeD.BodyNavigationExtensions3D"/>. Altruist
/// has no 2D navmesh / terrain provider today, so the <c>LaunchAlong</c> /
/// <c>LaunchToward</c> / terrain-aware overloads are 3D-only extras for now;
/// when 2D pathfinding lands, mirror at that time.
/// <para>Each method forwards to the <see cref="BodySteeringExtensions2D"/> overload taking the
/// target's <c>Transform.Position</c>, so it inherits that class's angle convention: the body's
/// counter-clockwise <see cref="IPhysxBody2D.RotationZ"/>, facing along its local +Y axis. Use these for
/// simple top-down steering of AI / kinematic
/// bodies toward another world object; for side-view physics bodies use
/// <see cref="GameplayVerbs2D"/> (<c>AimAt</c>, <c>HoldAngle</c>, <c>DriveAlong</c>) with
/// <c>Rotation2D</c> angles instead.</para></summary>
public static class BodyNavigationExtensions2D
{
    /// <summary>Snaps the body's rotation to face <paramref name="target"/>'s position
    /// (<see cref="BodySteeringExtensions2D.FaceToward(IPhysxBody2D,System.Numerics.Vector2)"/>: local +Y at the target).</summary>
    public static void FaceToward(this IPhysxBody2D body, IWorldObject2D target)
        => body.FaceToward(target.Transform.Position);

    /// <summary>Euclidean distance from the body's position to <paramref name="target"/>'s position, world units.</summary>
    public static float DistanceTo(this IPhysxBody2D body, IWorldObject2D target)
        => body.DistanceTo(target.Transform.Position);

    /// <summary>Whether <paramref name="target"/> lies within <paramref name="halfAngleDegrees"/>
    /// (degrees) of the body's facing (its local +Y axis); true when the positions coincide.</summary>
    public static bool IsFacing(this IPhysxBody2D body, IWorldObject2D target, float halfAngleDegrees)
        => body.IsFacing(target.Transform.Position, halfAngleDegrees);

    /// <summary>Rotates the body toward <paramref name="target"/> by at most
    /// <paramref name="maxAngularSpeedRadPerSec"/> * <paramref name="dt"/> radians (writes the angle
    /// directly, body rotation convention).</summary>
    /// <param name="body">The body to turn.</param>
    /// <param name="target">The object to turn toward.</param>
    /// <param name="maxAngularSpeedRadPerSec">Turn rate limit in rad/s.</param>
    /// <param name="dt">Step length in seconds.</param>
    public static void TurnToward(this IPhysxBody2D body, IWorldObject2D target,
                                  float maxAngularSpeedRadPerSec, float dt)
        => body.TurnToward(target.Transform.Position, maxAngularSpeedRadPerSec, dt);

    /// <summary>Sets the body's linear velocity to <paramref name="speed"/> (units/s) straight at
    /// <paramref name="target"/>, or stops it when already there or <paramref name="speed"/> &lt;= 0.
    /// <paramref name="dt"/> is unused (kept for symmetry).</summary>
    public static void MoveToward(this IPhysxBody2D body, IWorldObject2D target, float speed, float dt)
        => body.MoveToward(target.Transform.Position, speed, dt);
}
