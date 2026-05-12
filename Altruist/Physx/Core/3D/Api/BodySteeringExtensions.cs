/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD;

/// <summary>Ergonomic intent surface on <see cref="IPhysxBody3D"/>:
/// "face this", "turn toward this", "move toward this", "launch this".
/// All horizontal-motion methods preserve <c>LinearVelocity.Y</c> so
/// gravity / kinematic falls aren't clobbered. Built on top of the pure
/// math primitives in <see cref="Altruist.ThreeD.Numerics"/>; no new
/// physics path — <see cref="LaunchImpulse(IPhysxBody3D, Vector3, float)"/>
/// and friends route through the existing <see cref="PhysxForce"/> API.</summary>
public static class BodySteeringExtensions
{
    private const float CoincidentEpsilonSq = 1e-8f;

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>Yaw extracted from <see cref="IPhysxBody3D.Rotation"/>,
    /// using the canonical Altruist convention (<see cref="Yaw.Calculate"/>).</summary>
    public static float GetYaw(this IPhysxBody3D body) => Yaw.Calculate(body.Rotation);

    public static float HorizontalDistanceTo(this IPhysxBody3D body, Vector3 target)
        => Distance.Horizontal(body.Position, target);

    /// <summary>True iff <paramref name="target"/> is within
    /// <paramref name="halfAngleDegrees"/> of the body's facing direction
    /// in the XZ plane (range is unrestricted; combine with a separate
    /// distance check for full cone hit-tests).</summary>
    public static bool IsFacing(this IPhysxBody3D body, Vector3 target, float halfAngleDegrees)
    {
        var dx = target.X - body.Position.X;
        var dz = target.Z - body.Position.Z;
        if (dx * dx + dz * dz < CoincidentEpsilonSq) return true;
        var targetYaw = MathF.Atan2(dx, dz);
        var diff = Angle.ShortestDifference(body.GetYaw(), targetYaw);
        return MathF.Abs(diff) <= Angle.ToRadians(halfAngleDegrees);
    }

    // ── Snap facing ───────────────────────────────────────────────────────

    /// <summary>Instantly snap <see cref="IPhysxBody3D.Rotation"/> so the
    /// body faces <paramref name="worldPoint"/> in the XZ plane. No-op when
    /// the point is coincident with the body's position.</summary>
    public static void FaceToward(this IPhysxBody3D body, Vector3 worldPoint)
    {
        var dx = worldPoint.X - body.Position.X;
        var dz = worldPoint.Z - body.Position.Z;
        if (dx * dx + dz * dz < CoincidentEpsilonSq) return;
        body.Rotation = Rotation.Calculate(Yaw.FromDirection(dx, dz));
    }

    // ── Turning (angular-speed-clamped) ───────────────────────────────────

    /// <summary>Step the body's facing yaw toward <paramref name="worldPoint"/>
    /// by at most <paramref name="maxAngularSpeedRadPerSec"/> × <paramref name="dt"/>
    /// radians. Takes the short way around. Replaces hand-rolled
    /// "MoveTowardsAngleRadians + NormalizeRadians" patterns.</summary>
    public static void TurnToward(this IPhysxBody3D body, Vector3 worldPoint,
                                  float maxAngularSpeedRadPerSec, float dt)
    {
        var dx = worldPoint.X - body.Position.X;
        var dz = worldPoint.Z - body.Position.Z;
        if (dx * dx + dz * dz < CoincidentEpsilonSq) return;
        var targetYaw = Yaw.FromDirection(dx, dz);
        var newYaw = Angle.MoveToward(body.GetYaw(), targetYaw,
                                                 maxAngularSpeedRadPerSec * dt);
        body.Rotation = Rotation.Calculate(newYaw);
    }

    // ── Horizontal motion (preserves Y) ───────────────────────────────────

    /// <summary>Set <see cref="IPhysxBody3D.LinearVelocity"/> to a horizontal
    /// vector pointing at <paramref name="worldPoint"/> with magnitude
    /// <paramref name="speed"/>. Y component is preserved (gravity / kinematic
    /// falls untouched). The <paramref name="dt"/> parameter is accepted for
    /// API symmetry with <see cref="TurnToward"/>; velocity is set in
    /// world-units-per-second so dt isn't multiplied in here.</summary>
    public static void MoveTowardHorizontal(this IPhysxBody3D body, Vector3 worldPoint,
                                            float speed, float dt)
    {
        _ = dt;
        var dir = Direction.Horizontal(body.Position, worldPoint);
        if (dir == Vector3.Zero || speed <= 0f)
        {
            body.StopHorizontal();
            return;
        }
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector3(dir.X * speed, v.Y, dir.Z * speed);
    }

    /// <summary>Zero the X/Z components of <see cref="IPhysxBody3D.LinearVelocity"/>;
    /// preserve Y.</summary>
    public static void StopHorizontal(this IPhysxBody3D body)
    {
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector3(0f, v.Y, 0f);
    }

    // ── Launch verbs ──────────────────────────────────────────────────────

    /// <summary>Apply an impulse along <paramref name="direction"/> with
    /// <paramref name="magnitude"/>. <paramref name="direction"/> is normalized
    /// first; pass a non-unit vector freely. No-op on zero direction or
    /// non-positive magnitude.</summary>
    public static void LaunchImpulse(this IPhysxBody3D body, Vector3 direction, float magnitude)
    {
        if (magnitude <= 0f) return;
        var lenSq = direction.LengthSquared();
        if (lenSq < CoincidentEpsilonSq) return;
        var unit = direction / MathF.Sqrt(lenSq);
        body.ApplyForce(PhysxForce.Impulse3D(unit * magnitude));
    }

    /// <summary>Apply <paramref name="worldImpulse"/> directly (already in
    /// world-space, magnitude included).</summary>
    public static void LaunchImpulse(this IPhysxBody3D body, Vector3 worldImpulse)
    {
        if (worldImpulse.LengthSquared() < CoincidentEpsilonSq) return;
        body.ApplyForce(PhysxForce.Impulse3D(worldImpulse));
    }

    /// <summary>Set linear velocity directly to <paramref name="worldVelocity"/>
    /// via the engine-agnostic <see cref="PhysxForce.LinearVelocity3D"/> path
    /// (lets BEPU / Box2D providers route this through their preferred API).</summary>
    public static void LaunchVelocity(this IPhysxBody3D body, Vector3 worldVelocity)
        => body.ApplyForce(PhysxForce.LinearVelocity3D(worldVelocity));
}
