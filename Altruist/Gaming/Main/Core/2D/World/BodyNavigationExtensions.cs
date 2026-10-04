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
/// when 2D pathfinding lands, mirror at that time.</summary>
public static class BodyNavigationExtensions2D
{
    public static void FaceToward(this IPhysxBody2D body, IWorldObject2D target)
        => body.FaceToward(target.Transform.Position);

    public static float DistanceTo(this IPhysxBody2D body, IWorldObject2D target)
        => body.DistanceTo(target.Transform.Position);

    public static bool IsFacing(this IPhysxBody2D body, IWorldObject2D target, float halfAngleDegrees)
        => body.IsFacing(target.Transform.Position, halfAngleDegrees);

    public static void TurnToward(this IPhysxBody2D body, IWorldObject2D target,
                                  float maxAngularSpeedRadPerSec, float dt)
        => body.TurnToward(target.Transform.Position, maxAngularSpeedRadPerSec, dt);

    public static void MoveToward(this IPhysxBody2D body, IWorldObject2D target, float speed, float dt)
        => body.MoveToward(target.Transform.Position, speed, dt);
}
