/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Ergonomic intent surface on <see cref="IPhysxBody2D"/> — 2D mirror
/// of <see cref="Altruist.Physx.ThreeD.BodySteeringExtensions3D"/>. 2D has no
/// vertical axis, so <see cref="MoveToward(IPhysxBody2D,Vector2,float,float)"/>
/// writes the full <see cref="IPhysxBody2D.LinearVelocity"/> (no Y-preservation
/// gymnastics) and <see cref="Stop(IPhysxBody2D)"/> zeroes both components.</summary>
public static class BodySteeringExtensions2D
{
    private const float CoincidentEpsilonSq = 1e-8f;

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>The body's facing rotation in radians.</summary>
    public static float GetRotation(this IPhysxBody2D body) => body.RotationZ;

    public static float DistanceTo(this IPhysxBody2D body, Vector2 target)
        => Distance2D.Between(body.Position, target);
    public static float DistanceTo(this IPhysxBody2D body, Position2D target)
        => Distance2D.Between(body.Position, target.ToFloatVector2());

    public static bool IsFacing(this IPhysxBody2D body, Vector2 target, float halfAngleDegrees)
    {
        var dx = target.X - body.Position.X;
        var dy = target.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return true;
        var targetRot = MathF.Atan2(dx, dy);
        var diff = Angle.ShortestDifference(body.RotationZ, targetRot);
        return MathF.Abs(diff) <= Angle.ToRadians(halfAngleDegrees);
    }
    public static bool IsFacing(this IPhysxBody2D body, Position2D target, float halfAngleDegrees)
        => IsFacing(body, target.ToFloatVector2(), halfAngleDegrees);

    // ── Snap facing ───────────────────────────────────────────────────────

    public static void FaceToward(this IPhysxBody2D body, Vector2 worldPoint)
    {
        var dx = worldPoint.X - body.Position.X;
        var dy = worldPoint.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return;
        body.RotationZ = Yaw2D.FromDirection(dx, dy);
    }
    public static void FaceToward(this IPhysxBody2D body, Position2D worldPoint)
        => FaceToward(body, worldPoint.ToFloatVector2());

    // ── Turning (angular-speed-clamped) ───────────────────────────────────

    public static void TurnToward(this IPhysxBody2D body, Vector2 worldPoint,
                                  float maxAngularSpeedRadPerSec, float dt)
    {
        var dx = worldPoint.X - body.Position.X;
        var dy = worldPoint.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return;
        var targetRot = Yaw2D.FromDirection(dx, dy);
        body.RotationZ = Angle.MoveToward(body.RotationZ, targetRot, maxAngularSpeedRadPerSec * dt);
    }
    public static void TurnToward(this IPhysxBody2D body, Position2D worldPoint,
                                  float maxAngularSpeedRadPerSec, float dt)
        => TurnToward(body, worldPoint.ToFloatVector2(), maxAngularSpeedRadPerSec, dt);

    // ── Motion ────────────────────────────────────────────────────────────

    /// <summary>Set <see cref="IPhysxBody2D.LinearVelocity"/> to a vector
    /// pointing at <paramref name="worldPoint"/> with magnitude
    /// <paramref name="speed"/>. The <paramref name="dt"/> parameter is accepted
    /// for API symmetry; velocity is set in world-units-per-second so dt
    /// isn't multiplied in here.</summary>
    public static void MoveToward(this IPhysxBody2D body, Vector2 worldPoint,
                                  float speed, float dt)
    {
        _ = dt;
        var dir = Direction2D.Between(body.Position, worldPoint);
        if (dir == Vector2.Zero || speed <= 0f)
        {
            body.Stop();
            return;
        }
        body.LinearVelocity = new Vector2(dir.X * speed, dir.Y * speed);
    }
    public static void MoveToward(this IPhysxBody2D body, Position2D worldPoint,
                                  float speed, float dt)
        => MoveToward(body, worldPoint.ToFloatVector2(), speed, dt);

    public static void MoveTowardAngle(this IPhysxBody2D body, float rotationRadians,
                                       float speed, float dt)
    {
        _ = dt;
        if (speed <= 0f) { body.Stop(); return; }
        var dir = Direction2D.TowardAngle(rotationRadians);
        body.LinearVelocity = new Vector2(dir.X * speed, dir.Y * speed);
    }

    public static void Stop(this IPhysxBody2D body) => body.LinearVelocity = Vector2.Zero;

    // ── Launch verbs ──────────────────────────────────────────────────────

    public static void LaunchImpulse(this IPhysxBody2D body, Vector2 direction, float magnitude)
    {
        if (magnitude <= 0f) return;
        var lenSq = direction.LengthSquared();
        if (lenSq < CoincidentEpsilonSq) return;
        var unit = direction / MathF.Sqrt(lenSq);
        body.ApplyForce(PhysxForce.Impulse2D(unit * magnitude));
    }

    public static void LaunchImpulse(this IPhysxBody2D body, Vector2 worldImpulse)
    {
        if (worldImpulse.LengthSquared() < CoincidentEpsilonSq) return;
        body.ApplyForce(PhysxForce.Impulse2D(worldImpulse));
    }

    public static void LaunchVelocity(this IPhysxBody2D body, Vector2 worldVelocity)
        => body.ApplyForce(PhysxForce.LinearVelocity2D(worldVelocity));
}
