/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD;

/// <summary>Ergonomic intent surface on <see cref="IPhysxBody3D"/>:
/// "face this", "turn toward this", "move toward this", "launch this".
/// <see cref="MoveToward(IPhysxBody3D,Vector3,float,float)"/> and
/// <see cref="MoveTowardAngle(IPhysxBody3D,float,float,float)"/> set
/// horizontal velocity and preserve <c>LinearVelocity.Y</c> so gravity /
/// kinematic falls aren't clobbered; KCC-driven bodies consume that velocity
/// in their per-tick <c>Step()</c> and apply slope-aware sweeps. For
/// non-KCC kinematic bodies (mob AI) that need explicit terrain Y-snap,
/// see the <c>ITerrainProvider</c> overloads in
/// <c>Altruist.Gaming.ThreeD.BodyNavigationExtensions</c>. Built on top of
/// the pure math primitives in <see cref="Altruist.ThreeD.Numerics"/>; no
/// new physics path — <see cref="LaunchImpulse(IPhysxBody3D, Vector3, float)"/>
/// and friends route through the existing <see cref="PhysxForce"/> API.</summary>
/// <remarks>Conventions: +Y up; yaw is in radians about +Y, 0 faces +Z and positive yaw turns toward +X
/// (<c>yaw = atan2(dx, dz)</c>); speeds are world units per second, angular speeds radians per second,
/// <c>dt</c> in seconds. All writes go through the body's properties or <see cref="IPhysxBody.ApplyForce"/>, so BEPU
/// bodies are woken. For raw force/velocity access without intent semantics use <see cref="IPhysxApiProvider3D"/>.</remarks>
/// <example>
/// <code>
/// body.TurnToward(target, maxAngularSpeedRadPerSec: MathF.PI, dt);
/// if (body.IsFacing(target, halfAngleDegrees: 30f)) body.MoveToward(target, speed: 4f, dt);
/// </code>
/// </example>
public static class BodySteeringExtensions3D
{
    private const float CoincidentEpsilonSq = 1e-8f;

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>Yaw extracted from <see cref="IPhysxBody3D.Rotation"/>,
    /// using the canonical Altruist convention (<see cref="Yaw3D.Calculate"/>).</summary>
    public static float GetYaw(this IPhysxBody3D body) => Yaw3D.Calculate(body.Rotation);

    /// <summary>Full 3D distance from the body's position to <paramref name="target"/>.</summary>
    public static float DistanceTo(this IPhysxBody3D body, Vector3 target)
        => Distance3D.Between(body.Position, target);
    /// <summary><see cref="Position3D"/> overload of <see cref="DistanceTo(IPhysxBody3D, Vector3)"/>.</summary>
    public static float DistanceTo(this IPhysxBody3D body, Position3D target)
        => Distance3D.Between(body.Position, target.ToVector3());

    /// <summary>XZ-plane distance from the body's position to <paramref name="target"/> — the
    /// "range-check" variant used by combat / aggro tests where Y shouldn't matter.</summary>
    public static float HorizontalDistanceTo(this IPhysxBody3D body, Vector3 target)
        => Distance3D.Horizontal(body.Position, target);
    /// <summary><see cref="Position3D"/> overload of <see cref="HorizontalDistanceTo(IPhysxBody3D, Vector3)"/>.</summary>
    public static float HorizontalDistanceTo(this IPhysxBody3D body, Position3D target)
        => Distance3D.Horizontal(body.Position, target.ToVector3());

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
    /// <summary><see cref="Position3D"/> overload of <see cref="IsFacing(IPhysxBody3D, Vector3, float)"/>.</summary>
    public static bool IsFacing(this IPhysxBody3D body, Position3D target, float halfAngleDegrees)
        => IsFacing(body, target.ToVector3(), halfAngleDegrees);

    // ── Snap facing ───────────────────────────────────────────────────────

    /// <summary>Instantly snap <see cref="IPhysxBody3D.Rotation"/> so the
    /// body faces <paramref name="worldPoint"/> in the XZ plane. No-op when
    /// the point is coincident with the body's position.</summary>
    public static void FaceToward(this IPhysxBody3D body, Vector3 worldPoint)
    {
        var dx = worldPoint.X - body.Position.X;
        var dz = worldPoint.Z - body.Position.Z;
        if (dx * dx + dz * dz < CoincidentEpsilonSq) return;
        body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY,Yaw3D.FromDirection(dx, dz));
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="FaceToward(IPhysxBody3D, Vector3)"/>.</summary>
    public static void FaceToward(this IPhysxBody3D body, Position3D worldPoint)
        => FaceToward(body, worldPoint.ToVector3());

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
        var targetYaw = Yaw3D.FromDirection(dx, dz);
        var newYaw = Angle.MoveToward(body.GetYaw(), targetYaw,
                                                 maxAngularSpeedRadPerSec * dt);
        body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY,newYaw);
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="TurnToward(IPhysxBody3D, Vector3, float, float)"/>.</summary>
    public static void TurnToward(this IPhysxBody3D body, Position3D worldPoint,
                                  float maxAngularSpeedRadPerSec, float dt)
        => TurnToward(body, worldPoint.ToVector3(), maxAngularSpeedRadPerSec, dt);

    // ── Motion (writes velocity; KCC consumes for slope-aware sweep) ──────

    /// <summary>Set <see cref="IPhysxBody3D.LinearVelocity"/> to a horizontal
    /// vector pointing at <paramref name="worldPoint"/> with magnitude
    /// <paramref name="speed"/>. Y component is preserved (gravity / kinematic
    /// falls untouched). KCC-driven bodies consume this velocity in their
    /// per-tick <c>Step()</c> and produce terrain-aware motion (slope sweep,
    /// snap, depenetration). For non-KCC kinematic bodies that need explicit
    /// terrain Y-snap, use the <c>ITerrainProvider</c> overload in
    /// <c>BodyNavigationExtensions</c>. The <paramref name="dt"/> parameter
    /// is accepted for API symmetry with <see cref="TurnToward(IPhysxBody3D, Vector3, float, float)"/>; velocity
    /// is set in world-units-per-second so dt isn't multiplied in here.
    /// When the target is horizontally coincident or <paramref name="speed"/> ≤ 0 it calls <see cref="Stop"/> instead.</summary>
    public static void MoveToward(this IPhysxBody3D body, Vector3 worldPoint,
                                  float speed, float dt)
    {
        _ = dt;
        var dir = Direction3D.Horizontal(body.Position, worldPoint);
        if (dir == Vector3.Zero || speed <= 0f)
        {
            body.Stop();
            return;
        }
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector3(dir.X * speed, v.Y, dir.Z * speed);
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="MoveToward(IPhysxBody3D, Vector3, float, float)"/>.</summary>
    public static void MoveToward(this IPhysxBody3D body, Position3D worldPoint,
                                  float speed, float dt)
        => MoveToward(body, worldPoint.ToVector3(), speed, dt);

    /// <summary>Set <see cref="IPhysxBody3D.LinearVelocity"/> to a horizontal
    /// vector at <paramref name="yawRadians"/> with magnitude <paramref name="speed"/>.
    /// Y component is preserved. Use when AI wants to step in a direction
    /// without a concrete target (wander, knockback recoil, scripted patrol).
    /// Yaw 0 moves along +Z, positive yaw toward +X. <paramref name="speed"/> ≤ 0 calls <see cref="Stop"/>;
    /// <paramref name="dt"/> is unused (API symmetry).</summary>
    public static void MoveTowardAngle(this IPhysxBody3D body, float yawRadians,
                                       float speed, float dt)
    {
        _ = dt;
        if (speed <= 0f)
        {
            body.Stop();
            return;
        }
        var dir = Direction3D.TowardAngle(yawRadians);
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector3(dir.X * speed, v.Y, dir.Z * speed);
    }

    /// <summary>Zero the X/Z components of <see cref="IPhysxBody3D.LinearVelocity"/>;
    /// preserve Y so gravity / kinematic falls keep acting.</summary>
    public static void Stop(this IPhysxBody3D body)
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
