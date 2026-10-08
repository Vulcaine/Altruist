/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. Velocity and angular-velocity operations on a body: each reads the
/// body's state, applies one math-layer / <see cref="Velocity2D"/> formula and writes the result
/// back (one write per call). The gameplay verbs (<c>Altruist.Gaming.TwoD.GameplayVerbs2D</c>)
/// are built on these.
/// <para>Rotation convention: <see cref="IPhysxBody2D.RotationZ"/> counter-clockwise, as
/// <see cref="Rotation2D"/> and Box2D (local +Y is the body's "up").</para>
/// <para>Deterministic: same state and inputs give the same bits on the same runtime; each
/// method evaluates exactly the expression in its summary.</para></summary>
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

    /// <summary><c>LinearVelocity = VectorMath2D.AddAlong(v, direction, amount)</c>.</summary>
    public static void AddVelocityAlong(this IPhysxBody2D body, Vector2 direction, float amount) =>
        body.LinearVelocity = VectorMath2D.AddAlong(body.LinearVelocity, direction, amount);

    /// <summary><c>LinearVelocity = VectorMath2D.WithComponentAlong(v, direction, speed)</c>.</summary>
    public static void SetVelocityAlong(this IPhysxBody2D body, Vector2 direction, float speed) =>
        body.LinearVelocity = VectorMath2D.WithComponentAlong(body.LinearVelocity, direction, speed);

    /// <summary><c>LinearVelocity = VectorMath2D.ApproachComponentAlong(v, direction, target, acceleration * dt)</c>.</summary>
    public static void ApproachVelocityAlong(this IPhysxBody2D body, Vector2 direction, float target, float acceleration, float dt) =>
        body.LinearVelocity = VectorMath2D.ApproachComponentAlong(body.LinearVelocity, direction, target, acceleration * dt);

    /// <summary><c>LinearVelocity = VectorMath2D.RedirectAlong(v, axis)</c>.</summary>
    public static void RedirectVelocityAlong(this IPhysxBody2D body, Vector2 axis) =>
        body.LinearVelocity = VectorMath2D.RedirectAlong(body.LinearVelocity, axis);

    /// <summary><c>LinearVelocity = Velocity2D.AccelerateAlong(v, direction, acceleration, dt)</c>.</summary>
    public static void AccelerateAlong(this IPhysxBody2D body, Vector2 direction, float acceleration, float dt) =>
        body.LinearVelocity = Velocity2D.AccelerateAlong(body.LinearVelocity, direction, acceleration, dt);

    /// <summary><c>LinearVelocity = Velocity2D.CancelInto(v, normal, amount)</c>.</summary>
    public static void CancelVelocityInto(this IPhysxBody2D body, Vector2 normal, float amount = 1f) =>
        body.LinearVelocity = Velocity2D.CancelInto(body.LinearVelocity, normal, amount);

    /// <summary><c>LinearVelocity = Velocity2D.Bounce(incoming, normal, normalSpeed, tangentKeep)</c>
    /// with <c>incoming</c> the body's current velocity.</summary>
    public static void BounceVelocity(this IPhysxBody2D body, Vector2 normal, float normalSpeed, float tangentKeep) =>
        body.LinearVelocity = Velocity2D.Bounce(body.LinearVelocity, normal, normalSpeed, tangentKeep);

    /// <summary>Shortens the velocity by <paramref name="amount"/>:
    /// <c>speed = VectorMath2D.Length(v); LinearVelocity = VectorMath2D.ShortenBy(v, speed, amount)</c>
    /// (no-op at rest).</summary>
    public static void ReduceSpeed(this IPhysxBody2D body, float amount)
    {
        var v = body.LinearVelocity;
        var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (speed == 0) return;
        body.LinearVelocity = VectorMath2D.ShortenBy(v, speed, amount);
    }

    /// <summary><c>LinearVelocity = VectorMath2D.ClampLength(v, maxSpeed)</c>, written only when it
    /// was faster.</summary>
    public static void ClampSpeed(this IPhysxBody2D body, float maxSpeed)
    {
        var v = body.LinearVelocity;
        var s = v.Length();
        if (s > maxSpeed) body.LinearVelocity = v / s * maxSpeed;
    }

    /// <summary><c>LinearVelocity = Velocity2D.ApplyLinearDrag(v, drag, dt)</c>.</summary>
    public static void ApplyLinearDrag(this IPhysxBody2D body, float drag, float dt) =>
        body.LinearVelocity = Velocity2D.ApplyLinearDrag(body.LinearVelocity, drag, dt);

    /// <summary><c>LinearVelocity = Velocity2D.ApplyGravity(v, gravity, dt, scale)</c> (+Y up).</summary>
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

    /// <summary><see cref="BodyState2D.Capture"/>.</summary>
    public static BodyState2D CaptureState(this IPhysxBody2D body) => BodyState2D.Capture(body);
}
