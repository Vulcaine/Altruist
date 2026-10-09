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
/// gymnastics) and <see cref="Stop(IPhysxBody2D)"/> zeroes both components.
/// <para><b>Angle convention warning.</b> The facing helpers here (<see cref="IsFacing(IPhysxBody2D,Vector2,float)"/>,
/// <see cref="FaceToward(IPhysxBody2D,Vector2)"/>, <see cref="TurnToward(IPhysxBody2D,Vector2,float,float)"/>,
/// <see cref="MoveTowardAngle"/>) use the <see cref="Yaw2D"/> / <see cref="Direction2D.TowardAngle"/>
/// convention: angle 0 faces +Y and positive angles turn toward +X (clockwise, <c>Atan2(dx, dy)</c>,
/// direction <c>(Sin a, Cos a)</c>). That is the mirror image of the counter-clockwise
/// <see cref="IPhysxBody2D.RotationZ"/> used by Box2D, <see cref="Rotation2D"/> and
/// <see cref="BodyMotionExtensions2D"/>: on a Box2D body, <c>FaceToward</c> points the body's local +Y
/// at the target only when the target is straight up or down. For physics-accurate turning of
/// Box2D bodies use <see cref="BodyMotionExtensions2D.RotateTowardAngle"/> /
/// <see cref="BodyMotionExtensions2D.SteerAngularVelocity"/> with <see cref="Rotation2D"/> angles.</para>
/// <para>Side effects: writes go straight to <c>RotationZ</c> (teleport-rotate) or <c>LinearVelocity</c>;
/// the impulse / velocity verbs go through <see cref="IPhysxBody.ApplyForce"/> (Box2D wakes the
/// body). For mechanical velocity formulas use <see cref="BodyMotionExtensions2D"/>; for intent verbs
/// the Gaming package's <c>GameplayVerbs2D</c>.</para></summary>
public static class BodySteeringExtensions2D
{
    private const float CoincidentEpsilonSq = 1e-8f;

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>The body's facing rotation in radians.</summary>
    public static float GetRotation(this IPhysxBody2D body) => body.RotationZ;

    /// <summary>Euclidean distance from the body's origin to <paramref name="target"/> (<c>Vector2.Distance</c>).</summary>
    public static float DistanceTo(this IPhysxBody2D body, Vector2 target)
        => Distance2D.Between(body.Position, target);
    /// <summary><see cref="DistanceTo(IPhysxBody2D,Vector2)"/> for a <see cref="Position2D"/>.</summary>
    public static float DistanceTo(this IPhysxBody2D body, Position2D target)
        => Distance2D.Between(body.Position, target.ToFloatVector2());

    /// <summary>Whether the target lies within ±<paramref name="halfAngleDegrees"/> (degrees) of the
    /// body's facing, with the facing angle read in the <see cref="Yaw2D"/> convention (see the class
    /// summary). True when the target coincides with the body origin (within 1e-4 units).</summary>
    /// <param name="body">The body.</param>
    /// <param name="target">World point.</param>
    /// <param name="halfAngleDegrees">Half cone angle in degrees.</param>
    public static bool IsFacing(this IPhysxBody2D body, Vector2 target, float halfAngleDegrees)
    {
        var dx = target.X - body.Position.X;
        var dy = target.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return true;
        var targetRot = MathF.Atan2(dx, dy);
        var diff = Angle.ShortestDifference(body.RotationZ, targetRot);
        return MathF.Abs(diff) <= Angle.ToRadians(halfAngleDegrees);
    }
    /// <summary><see cref="IsFacing(IPhysxBody2D,Vector2,float)"/> for a <see cref="Position2D"/>.</summary>
    public static bool IsFacing(this IPhysxBody2D body, Position2D target, float halfAngleDegrees)
        => IsFacing(body, target.ToFloatVector2(), halfAngleDegrees);

    // ── Snap facing ───────────────────────────────────────────────────────

    /// <summary>Snaps <see cref="IPhysxBody2D.RotationZ"/> to <c>Yaw2D.FromDirection(dx, dy)</c> toward
    /// <paramref name="worldPoint"/> (<see cref="Yaw2D"/> convention, see the class summary); no-op when the
    /// point coincides with the body origin. Teleport-rotates (no angular velocity).</summary>
    public static void FaceToward(this IPhysxBody2D body, Vector2 worldPoint)
    {
        var dx = worldPoint.X - body.Position.X;
        var dy = worldPoint.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return;
        body.RotationZ = Yaw2D.FromDirection(dx, dy);
    }
    /// <summary><see cref="FaceToward(IPhysxBody2D,Vector2)"/> for a <see cref="Position2D"/>.</summary>
    public static void FaceToward(this IPhysxBody2D body, Position2D worldPoint)
        => FaceToward(body, worldPoint.ToFloatVector2());

    // ── Turning (angular-speed-clamped) ───────────────────────────────────

    /// <summary>Rotates <see cref="IPhysxBody2D.RotationZ"/> toward the <see cref="Yaw2D"/> angle of
    /// <paramref name="worldPoint"/> by at most <c>maxAngularSpeedRadPerSec * dt</c>, the short way
    /// (<see cref="Angle.MoveToward"/>; the result is normalized to [-π, π]). No-op when the point
    /// coincides with the body origin. Writes the angle directly (no angular velocity).</summary>
    /// <param name="body">The body.</param>
    /// <param name="worldPoint">World point to face.</param>
    /// <param name="maxAngularSpeedRadPerSec">Turn rate limit in rad/s.</param>
    /// <param name="dt">Step in seconds.</param>
    public static void TurnToward(this IPhysxBody2D body, Vector2 worldPoint,
                                  float maxAngularSpeedRadPerSec, float dt)
    {
        var dx = worldPoint.X - body.Position.X;
        var dy = worldPoint.Y - body.Position.Y;
        if (dx * dx + dy * dy < CoincidentEpsilonSq) return;
        var targetRot = Yaw2D.FromDirection(dx, dy);
        body.RotationZ = Angle.MoveToward(body.RotationZ, targetRot, maxAngularSpeedRadPerSec * dt);
    }
    /// <summary><see cref="TurnToward(IPhysxBody2D,Vector2,float,float)"/> for a <see cref="Position2D"/>.</summary>
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
    /// <summary><see cref="MoveToward(IPhysxBody2D,Vector2,float,float)"/> for a <see cref="Position2D"/>.</summary>
    public static void MoveToward(this IPhysxBody2D body, Position2D worldPoint,
                                  float speed, float dt)
        => MoveToward(body, worldPoint.ToFloatVector2(), speed, dt);

    /// <summary>Sets <see cref="IPhysxBody2D.LinearVelocity"/> to <paramref name="speed"/> along
    /// <c>Direction2D.TowardAngle(rotationRadians)</c> = <c>(Sin a, Cos a)</c> (<see cref="Yaw2D"/>
    /// convention, not <see cref="IPhysxBody2D.RotationZ"/>'s); stops the body when
    /// <paramref name="speed"/> ≤ 0. <paramref name="dt"/> is unused.</summary>
    /// <param name="body">The body.</param>
    /// <param name="rotationRadians">Heading in the <see cref="Yaw2D"/> convention.</param>
    /// <param name="speed">Speed in units/s.</param>
    /// <param name="dt">Unused (API symmetry).</param>
    public static void MoveTowardAngle(this IPhysxBody2D body, float rotationRadians,
                                       float speed, float dt)
    {
        _ = dt;
        if (speed <= 0f) { body.Stop(); return; }
        var dir = Direction2D.TowardAngle(rotationRadians);
        body.LinearVelocity = new Vector2(dir.X * speed, dir.Y * speed);
    }

    /// <summary>Zeroes <see cref="IPhysxBody2D.LinearVelocity"/> (angular velocity is kept).</summary>
    public static void Stop(this IPhysxBody2D body) => body.LinearVelocity = Vector2.Zero;

    // ── Launch verbs ──────────────────────────────────────────────────────

    /// <summary>Applies a linear impulse of <paramref name="magnitude"/> (mass × units/s) along
    /// <paramref name="direction"/> (normalized here) at the center of mass; the resulting velocity
    /// change depends on mass. No-op for magnitude ≤ 0 or a near-zero direction. To set a velocity
    /// change independent of mass use <see cref="BodyMotionExtensions2D.AddVelocityAlong"/>.</summary>
    /// <param name="body">The body.</param>
    /// <param name="direction">Direction (any length).</param>
    /// <param name="magnitude">Impulse magnitude.</param>
    public static void LaunchImpulse(this IPhysxBody2D body, Vector2 direction, float magnitude)
    {
        if (magnitude <= 0f) return;
        var lenSq = direction.LengthSquared();
        if (lenSq < CoincidentEpsilonSq) return;
        var unit = direction / MathF.Sqrt(lenSq);
        body.ApplyForce(PhysxForce.Impulse2D(unit * magnitude));
    }

    /// <summary>Applies the world-space linear impulse <paramref name="worldImpulse"/> at the center of
    /// mass (no-op when near zero).</summary>
    public static void LaunchImpulse(this IPhysxBody2D body, Vector2 worldImpulse)
    {
        if (worldImpulse.LengthSquared() < CoincidentEpsilonSq) return;
        body.ApplyForce(PhysxForce.Impulse2D(worldImpulse));
    }

    /// <summary>Sets the linear velocity through <see cref="IPhysxBody.ApplyForce"/>
    /// (<c>PhysxForce.LinearVelocity2D</c>); on a Box2D body the same as assigning
    /// <see cref="IPhysxBody2D.LinearVelocity"/>.</summary>
    public static void LaunchVelocity(this IPhysxBody2D body, Vector2 worldVelocity)
        => body.ApplyForce(PhysxForce.LinearVelocity2D(worldVelocity));
}
