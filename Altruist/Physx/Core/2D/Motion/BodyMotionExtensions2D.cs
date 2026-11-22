/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. Velocity and angular-velocity operations on a body: each reads the
/// body's state, applies one math-layer formula (<see cref="VectorMath2D"/>, <see cref="Velocity2D"/>,
/// <see cref="Angle"/>, <see cref="Scalar"/>) and writes the result back (one write per call).
/// <para><b>Which layer to use.</b> Use these when you hold an <see cref="IPhysxBody2D"/> and want a
/// mechanical velocity change. Use <see cref="Velocity2D"/> / <see cref="VectorMath2D"/> directly when
/// you work on a velocity value that is not (yet) a body's (prediction, a captured incoming velocity).
/// The Gaming package's <c>GameplayVerbs2D</c> wraps these in intent names (e.g.
/// <c>GameplayVerbs2D.PushAlong</c> = <see cref="AddVelocityAlong"/>,
/// <c>GameplayVerbs2D.SetSpeedAlong</c> = <see cref="SetVelocityAlong"/>,
/// <c>GameplayVerbs2D.BounceOff</c> = <see cref="BounceVelocity"/>,
/// <c>GameplayVerbs2D.Fall</c> = <see cref="ApplyGravity"/>); prefer those in gameplay code.</para>
/// <para>Side effects: on a Box2D body (<see cref="Body2DAdapter"/>) a write to
/// <c>LinearVelocity</c> / <c>AngularVelocityZ</c> goes through Box2D's
/// <c>SetLinearVelocity</c> / <c>SetAngularVelocity</c>, which (as in Box2D) wake a sleeping body
/// when the new value is non-zero and are ignored by static bodies. Methods documented as
/// "written only when ..." skip the write (and so the wake) otherwise. No allocations.</para>
/// <para>Rotation convention: <see cref="IPhysxBody2D.RotationZ"/> in radians, counter-clockwise
/// positive, as <see cref="Rotation2D"/> and Box2D (local +Y is the body's "up"); +Y is world up;
/// <c>dt</c> in seconds.</para>
/// <para>Deterministic: same state and inputs give the same bits on the same runtime; each
/// method evaluates exactly the expression in its summary (float32, <c>MathF</c>). The TypeScript
/// twin is <c>@altruist/sim2d</c> <c>physics/bodyMotion2D.ts</c>, with the same operation order.</para></summary>
public static class BodyMotionExtensions2D
{
    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>Velocity component along the unit <paramref name="direction"/>:
    /// <c>v.X * d.X + v.Y * d.Y</c>.</summary>
    public static float SpeedAlong(this IPhysxBody2D body, Vector2 direction)
    {
        var v = body.LinearVelocity;
        return v.X * direction.X + v.Y * direction.Y;
    }

    /// <summary>Velocity of a world point of the body:
    /// <c>Velocity2D.PointVelocity(LinearVelocity, AngularVelocityZ, worldPoint - WorldCenter)</c>.</summary>
    public static Vector2 VelocityAtPoint(this IPhysxBody2D body, Vector2 worldPoint) =>
        Velocity2D.PointVelocity(body.LinearVelocity, body.AngularVelocityZ, worldPoint - body.WorldCenter);

    /// <summary>Is the world point inside the body's centered box (half extents plus padding)?
    /// <c>Geometry2D.BoxContainsLocal(GetLocalPoint(p), halfWidth + padX, halfHeight + padY)</c>.</summary>
    public static bool BoxContains(this IPhysxBody2D body, Vector2 worldPoint, float halfWidth, float halfHeight,
                                   float padX = 0f, float padY = 0f) =>
        Geometry2D.BoxContainsLocal(body.GetLocalPoint(worldPoint), halfWidth + padX, halfHeight + padY);

    /// <summary>Squared distance from the world point to the body's centered box (0 inside):
    /// <c>Geometry2D.BoxDistanceSquaredLocal(GetLocalPoint(p), halfWidth, halfHeight)</c>.</summary>
    public static float BoxDistanceSquared(this IPhysxBody2D body, Vector2 worldPoint, float halfWidth, float halfHeight) =>
        Geometry2D.BoxDistanceSquaredLocal(body.GetLocalPoint(worldPoint), halfWidth, halfHeight);

    // ── Linear velocity ───────────────────────────────────────────────────

    /// <summary>Adds <paramref name="amount"/> along the unit <paramref name="direction"/>, keeping the
    /// rest of the motion: <c>LinearVelocity = VectorMath2D.AddAlong(v, direction, amount)</c>.
    /// Alternatives: <see cref="SetVelocityAlong"/> sets that component to an exact value instead;
    /// <c>GameplayVerbs2D.LaunchAlong</c> (Gaming) replaces the whole velocity.</summary>
    public static void AddVelocityAlong(this IPhysxBody2D body, Vector2 direction, float amount) =>
        body.LinearVelocity = VectorMath2D.AddAlong(body.LinearVelocity, direction, amount);

    /// <summary>Sets the component along the unit <paramref name="direction"/> to exactly
    /// <paramref name="speed"/>, keeping the perpendicular part:
    /// <c>LinearVelocity = VectorMath2D.WithComponentAlong(v, direction, speed)</c>. To ease toward a
    /// target speed instead use <see cref="ApproachVelocityAlong"/>.</summary>
    public static void SetVelocityAlong(this IPhysxBody2D body, Vector2 direction, float speed) =>
        body.LinearVelocity = VectorMath2D.WithComponentAlong(body.LinearVelocity, direction, speed);

    /// <summary>Moves the component along the unit <paramref name="direction"/> toward
    /// <paramref name="target"/> by at most <c>acceleration * dt</c> (units/s² times seconds):
    /// <c>LinearVelocity = VectorMath2D.ApproachComponentAlong(v, direction, target, acceleration * dt)</c>.</summary>
    public static void ApproachVelocityAlong(this IPhysxBody2D body, Vector2 direction, float target, float acceleration, float dt) =>
        body.LinearVelocity = VectorMath2D.ApproachComponentAlong(body.LinearVelocity, direction, target, acceleration * dt);

    /// <summary>Re-points the velocity along <paramref name="axis"/> (e.g. follow a surface tangent):
    /// <c>LinearVelocity = VectorMath2D.RedirectAlong(v, axis)</c>; see that method for the exact rule.</summary>
    public static void RedirectVelocityAlong(this IPhysxBody2D body, Vector2 axis) =>
        body.LinearVelocity = VectorMath2D.RedirectAlong(body.LinearVelocity, axis);

    /// <summary>Constant acceleration along a direction for one step (no target, no cap):
    /// <c>LinearVelocity = Velocity2D.AccelerateAlong(v, direction, acceleration, dt)</c>.</summary>
    public static void AccelerateAlong(this IPhysxBody2D body, Vector2 direction, float acceleration, float dt) =>
        body.LinearVelocity = Velocity2D.AccelerateAlong(body.LinearVelocity, direction, acceleration, dt);

    /// <summary>Removes <paramref name="amount"/> (1 = all) of the motion into the unit
    /// <paramref name="normal"/>: <c>LinearVelocity = Velocity2D.CancelInto(v, normal, amount)</c>.
    /// Always writes (also when unchanged).</summary>
    public static void CancelVelocityInto(this IPhysxBody2D body, Vector2 normal, float amount = 1f) =>
        body.LinearVelocity = Velocity2D.CancelInto(body.LinearVelocity, normal, amount);

    /// <summary><c>LinearVelocity = Velocity2D.Bounce(incoming, normal, normalSpeed, tangentKeep)</c>
    /// with <c>incoming</c> the body's current velocity. To bounce from a velocity recorded earlier
    /// (e.g. captured in pre-solve, before the solver changed it) write
    /// <c>LinearVelocity = Velocity2D.Bounce(incoming, ...)</c> or use the Gaming package's
    /// <c>GameplayVerbs2D.BounceOff</c> overload that takes <c>incoming</c>.</summary>
    public static void BounceVelocity(this IPhysxBody2D body, Vector2 normal, float normalSpeed, float tangentKeep) =>
        body.LinearVelocity = Velocity2D.Bounce(body.LinearVelocity, normal, normalSpeed, tangentKeep);

    /// <summary>Shortens the velocity by <paramref name="amount"/>:
    /// <c>speed = VectorMath2D.Length(v); LinearVelocity = VectorMath2D.ShortenBy(v, speed, amount)</c>
    /// (no-op at rest). The length is <c>MathF.Sqrt(v.X * v.X + v.Y * v.Y)</c>.</summary>
    public static void ReduceSpeed(this IPhysxBody2D body, float amount)
    {
        var v = body.LinearVelocity;
        var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (speed == 0) return;
        body.LinearVelocity = VectorMath2D.ShortenBy(v, speed, amount);
    }

    /// <summary>Caps the speed at <paramref name="maxSpeed"/>: when <c>s = v.Length()</c> exceeds it,
    /// <c>LinearVelocity = v / s * maxSpeed</c> (written only when it was faster). Note the operation
    /// order (<c>v / s * maxSpeed</c>, <c>Vector2.Length</c>) when matching it elsewhere.</summary>
    public static void ClampSpeed(this IPhysxBody2D body, float maxSpeed)
    {
        var v = body.LinearVelocity;
        var s = v.Length();
        if (s > maxSpeed) body.LinearVelocity = v / s * maxSpeed;
    }

    /// <summary>Hand-applied linear drag for one step:
    /// <c>LinearVelocity = Velocity2D.ApplyLinearDrag(v, drag, dt)</c>. For engine-side damping use
    /// <see cref="PhysxBodyDef2D.LinearDamping"/>.</summary>
    public static void ApplyLinearDrag(this IPhysxBody2D body, float drag, float dt) =>
        body.LinearVelocity = Velocity2D.ApplyLinearDrag(body.LinearVelocity, drag, dt);

    /// <summary>Hand-applied gravity for one step (for worlds with zero engine gravity):
    /// <c>LinearVelocity = Velocity2D.ApplyGravity(v, gravity, dt, scale)</c> (+Y up,
    /// <paramref name="gravity"/> ≥ 0 pulls toward -Y).</summary>
    public static void ApplyGravity(this IPhysxBody2D body, float gravity, float dt, float scale = 1f) =>
        body.LinearVelocity = Velocity2D.ApplyGravity(body.LinearVelocity, gravity, dt, scale);

    /// <summary><c>Velocity2D.KeepMinimumSpeedX(v, minSpeed, fallbackDirection)</c>, written only when
    /// it changed the velocity.</summary>
    public static void KeepMinimumSpeedX(this IPhysxBody2D body, float minSpeed, int fallbackDirection)
    {
        var v = body.LinearVelocity;
        if (!(MathF.Abs(v.X) < minSpeed)) return;
        body.LinearVelocity = Velocity2D.KeepMinimumSpeedX(v, minSpeed, fallbackDirection);
    }

    /// <summary>Keeps only <paramref name="keep"/> of the velocity change since
    /// (<paramref name="linearBefore"/>, <paramref name="angularBefore"/>):
    /// <c>LinearVelocity = before + (now - before) * keep; AngularVelocityZ = wBefore + (wNow - wBefore) * keep</c>.</summary>
    public static void BlendMotionFrom(this IPhysxBody2D body, Vector2 linearBefore, float angularBefore, float keep)
    {
        body.LinearVelocity = VectorMath2D.Lerp(linearBefore, body.LinearVelocity, keep);
        body.AngularVelocityZ = Scalar.Lerp(angularBefore, body.AngularVelocityZ, keep);
    }

    /// <summary>Sets the velocity from its parts in a contact / surface frame:
    /// <c>LinearVelocity = frame.Compose(alongNormal, alongTangent)</c>
    /// (<c>Normal * alongNormal + Tangent * alongTangent</c>).</summary>
    public static void SetVelocityInFrame(this IPhysxBody2D body, NormalFrame2D frame, float alongNormal, float alongTangent) =>
        body.LinearVelocity = frame.Compose(alongNormal, alongTangent);

    // ── Angular velocity ──────────────────────────────────────────────────

    /// <summary>Turns the body's up (local +Y) toward the <paramref name="normal"/>:
    /// <c>AngularVelocityZ = Angle.Wrap(Rotation2D.AngleAligningUp(normal) - RotationZ) * rate</c>.</summary>
    public static void AlignUpToNormal(this IPhysxBody2D body, Vector2 normal, float rate) =>
        body.AngularVelocityZ = Angle.Wrap(Rotation2D.AngleAligningUp(normal) - body.RotationZ) * rate;

    /// <summary><c>AngularVelocityZ = Angle.ArriveRate(RotationZ, targetAngle, time, minTime)</c>.</summary>
    public static void ArriveAtAngle(this IPhysxBody2D body, float targetAngle, float time, float minTime) =>
        body.AngularVelocityZ = Angle.ArriveRate(body.RotationZ, targetAngle, time, minTime);

    /// <summary><c>AngularVelocityZ = Angle.RateToward(RotationZ, targetAngle, gain)</c>.</summary>
    public static void RotateTowardAngle(this IPhysxBody2D body, float targetAngle, float gain) =>
        body.AngularVelocityZ = Angle.RateToward(body.RotationZ, targetAngle, gain);

    /// <summary><c>AngularVelocityZ = Scalar.Approach(AngularVelocityZ, targetRate, angularAcceleration * dt)</c>.</summary>
    public static void ApproachAngularVelocity(this IPhysxBody2D body, float targetRate, float angularAcceleration, float dt) =>
        body.AngularVelocityZ = Scalar.Approach(body.AngularVelocityZ, targetRate, angularAcceleration * dt);

    /// <summary>Clamped proportional steering with an acceleration limit:
    /// <c>w = Angle.ClampedRateToward(RotationZ, targetAngle, gain, maxRate);
    /// AngularVelocityZ = Scalar.Approach(AngularVelocityZ, w, angularAcceleration * dt)</c>.</summary>
    public static void SteerAngularVelocity(this IPhysxBody2D body, float targetAngle, float gain, float maxRate,
                                            float angularAcceleration, float dt)
    {
        var w = Angle.ClampedRateToward(body.RotationZ, targetAngle, gain, maxRate);
        body.AngularVelocityZ = Scalar.Approach(body.AngularVelocityZ, w, angularAcceleration * dt);
    }

    /// <summary>Adds the spin a surface slip of <paramref name="slip"/> (tangential speed difference)
    /// gives a round body of <paramref name="radius"/>, scaled by <paramref name="factor"/>:
    /// <c>AngularVelocityZ = AngularVelocityZ + slip / radius * factor</c>.</summary>
    public static void AddSpinFromSlip(this IPhysxBody2D body, float slip, float radius, float factor) =>
        body.AngularVelocityZ = body.AngularVelocityZ + slip / radius * factor;

    // ── State ─────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="source"/>'s transform and velocities and wakes the body:
    /// <c>SetTransform(src.Position, src.RotationZ); LinearVelocity; AngularVelocityZ; IsAwake = true</c>.</summary>
    public static void CopyMotionFrom(this IPhysxBody2D body, IPhysxBody2D source)
    {
        body.SetTransform(source.Position, source.RotationZ);
        body.LinearVelocity = source.LinearVelocity;
        body.AngularVelocityZ = source.AngularVelocityZ;
        body.IsAwake = true;
    }

    /// <summary>Snapshot of the body's motion state: <see cref="BodyState2D.Capture"/>. Restore it with
    /// <see cref="BodyState2D.Restore"/>.</summary>
    public static BodyState2D CaptureState(this IPhysxBody2D body) => BodyState2D.Capture(body);
}
