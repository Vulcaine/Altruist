/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;
using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD;

/// <summary>Gameplay layer. Designer-level actions and questions on a 2D body, so game code reads
/// like its rules (<c>body.JumpOff(ground, jumpSpeed)</c>, <c>body.AlignToSurface(ground, rate)</c>,
/// <c>ball.Fall(gravity, dt, scale)</c>) instead of vector algebra.
/// <para>Layers: every verb delegates to one physics-layer operation
/// (<see cref="BodyMotionExtensions2D"/>, <see cref="Velocity2D"/>, <see cref="Ballistics2D"/>,
/// <see cref="Kinematics"/>), which delegates to the math layer (<c>Altruist.Numerics</c>,
/// <c>Altruist.TwoD.Numerics</c>). Nothing is re-derived on the way, so a verb produces exactly the
/// bits of the formula in its summary (deterministic: same state and inputs, same bits on the same
/// runtime).</para>
/// <para>Conventions: side view, +Y up, <c>gravity</c> = magnitude of the pull toward -Y;
/// rotations counter-clockwise with the body's local +Y as its "up" (Box2D).</para></summary>
public static class GameplayVerbs2D
{
    // ── Launching and jumping ─────────────────────────────────────────────

    /// <summary>Jump off a surface: cancel any motion into it, then add
    /// <paramref name="speed"/> straight out along its unit <paramref name="surfaceNormal"/>
    /// (any normal works, e.g. the body's own up for an air jump):
    /// <c>v = Velocity2D.CancelInto(v, n); v = VectorMath2D.AddAlong(v, n, speed)</c>.</summary>
    public static void JumpOff(this IPhysxBody2D body, Vector2 surfaceNormal, float speed)
    {
        var v = Velocity2D.CancelInto(body.LinearVelocity, surfaceNormal);
        body.LinearVelocity = VectorMath2D.AddAlong(v, surfaceNormal, speed);
    }

    /// <summary>
    /// Jump as if the body already faced where it is being aimed: <c>JumpOff(Rotation2D.UpAt(aimedRotation), speed)</c>.
    /// For fast play, where the jump comes before a rate-limited turn has finished: the jump follows the
    /// player's aim (e.g. the stick) instead of the half-turned body, so "aim, then jump" acts on the aim.
    /// </summary>
    public static void JumpAsAimed(this IPhysxBody2D body, float aimedRotation, float speed) =>
        body.JumpOff(Rotation2D.UpAt(aimedRotation), speed);

    /// <summary>Add speed in a direction, keeping the current motion:
    /// <c>BodyMotionExtensions2D.AddVelocityAlong(direction, speed)</c>.</summary>
    public static void PushAlong(this IPhysxBody2D body, Vector2 direction, float speed) =>
        body.AddVelocityAlong(direction, speed);

    /// <summary>Replace the motion with <paramref name="speed"/> along the unit
    /// <paramref name="direction"/>: <c>LinearVelocity = (d.X * speed, d.Y * speed)</c>.</summary>
    public static void LaunchAlong(this IPhysxBody2D body, Vector2 direction, float speed) =>
        body.LinearVelocity = new Vector2(direction.X * speed, direction.Y * speed);

    /// <summary><see cref="LaunchAlong"/> the body's own <paramref name="localForward"/> axis
    /// (e.g. (1, 0)) turned into world space.</summary>
    public static void LaunchForward(this IPhysxBody2D body, Vector2 localForward, float speed) =>
        body.LaunchAlong(body.GetWorldVector(localForward), speed);

    /// <summary>Cancel <paramref name="fraction"/> (1 = all) of the motion that goes against
    /// <paramref name="direction"/> (before a thrust along it):
    /// <c>BodyMotionExtensions2D.CancelVelocityInto(direction, fraction)</c>.</summary>
    public static void CancelMotionAgainst(this IPhysxBody2D body, Vector2 direction, float fraction = 1f) =>
        body.CancelVelocityInto(direction, fraction);

    /// <summary>Bounce or launch off a surface with the body's current velocity as the incoming one:
    /// <paramref name="outSpeed"/> out along the unit <paramref name="normal"/> plus
    /// <paramref name="tangentKeep"/> of the motion along the surface
    /// (<see cref="Velocity2D.Bounce"/>).</summary>
    public static void BounceOff(this IPhysxBody2D body, Vector2 normal, float outSpeed, float tangentKeep) =>
        body.BounceVelocity(normal, outSpeed, tangentKeep);

    /// <summary><see cref="BounceOff(IPhysxBody2D,Vector2,float,float)"/> from a recorded
    /// <paramref name="incoming"/> velocity (e.g. captured at the contact, before the solver):
    /// <c>LinearVelocity = Velocity2D.Bounce(incoming, normal, outSpeed, tangentKeep)</c>.</summary>
    public static void BounceOff(this IPhysxBody2D body, Vector2 normal, float outSpeed, float tangentKeep, Vector2 incoming) =>
        body.LinearVelocity = Velocity2D.Bounce(incoming, normal, outSpeed, tangentKeep);

    /// <summary>Keep only <paramref name="keep"/> (recoil, 0..1) of the velocity change a hit caused
    /// since (<paramref name="velocityBefore"/>, <paramref name="spinBefore"/>):
    /// <see cref="BodyMotionExtensions2D.BlendMotionFrom"/>.</summary>
    public static void AbsorbImpact(this IPhysxBody2D body, Vector2 velocityBefore, float spinBefore, float keep) =>
        body.BlendMotionFrom(velocityBefore, spinBefore, keep);

    // ── Horizontal / vertical motion ──────────────────────────────────────
    //
    // Each writes one component directly and keeps the other as it is (a component along (1, 0) or
    // (0, 1) through SetSpeedAlong would round differently).

    /// <summary>Set the horizontal speed, keeping the vertical: <c>LinearVelocity = (x, v.Y)</c>.</summary>
    public static void SetVelocityX(this IPhysxBody2D body, float x) =>
        body.LinearVelocity = new Vector2(x, body.LinearVelocity.Y);

    /// <summary>Set the vertical speed, keeping the horizontal: <c>LinearVelocity = (v.X, y)</c>.</summary>
    public static void SetVelocityY(this IPhysxBody2D body, float y) =>
        body.LinearVelocity = new Vector2(body.LinearVelocity.X, y);

    /// <summary>Scale the horizontal speed (e.g. cut the run-up when launching upward):
    /// <c>LinearVelocity = (v.X * factor, v.Y)</c>.</summary>
    public static void ScaleVelocityX(this IPhysxBody2D body, float factor)
    {
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector2(v.X * factor, v.Y);
    }

    /// <summary>Scale the vertical speed (e.g. damp the lift of a burst):
    /// <c>LinearVelocity = (v.X, v.Y * factor)</c>.</summary>
    public static void ScaleVelocityY(this IPhysxBody2D body, float factor)
    {
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector2(v.X, v.Y * factor);
    }

    /// <summary>Keep the vertical speed within [<paramref name="min"/>, <paramref name="max"/>] (e.g.
    /// no fall and at most a small hop): <c>LinearVelocity = (v.X, Scalar.ClampMinMax(v.Y, min, max))</c>,
    /// i.e. <c>MathF.Min(MathF.Max(v.Y, min), max)</c>.</summary>
    public static void ClampVelocityY(this IPhysxBody2D body, float min, float max)
    {
        var v = body.LinearVelocity;
        body.LinearVelocity = new Vector2(v.X, Scalar.ClampMinMax(v.Y, min, max));
    }

    // ── Driving on surfaces ───────────────────────────────────────────────

    /// <summary>Speed up or slow down along the unit <paramref name="tangent"/> toward
    /// <paramref name="targetSpeed"/> at most <paramref name="acceleration"/> (per second), leaving
    /// the motion across it alone:
    /// <c>VectorMath2D.ApproachComponentAlong(v, tangent, targetSpeed, acceleration * dt)</c>.</summary>
    public static void DriveAlong(this IPhysxBody2D body, Vector2 tangent, float targetSpeed, float acceleration, float dt) =>
        body.ApproachVelocityAlong(tangent, targetSpeed, acceleration, dt);

    /// <summary>Brake along <paramref name="direction"/> toward a stop:
    /// <c>DriveAlong(direction, 0, deceleration, dt)</c>.</summary>
    public static void BrakeAlong(this IPhysxBody2D body, Vector2 direction, float deceleration, float dt) =>
        body.ApproachVelocityAlong(direction, 0, deceleration, dt);

    /// <summary>Set the speed along <paramref name="direction"/>, keeping the rest:
    /// <c>VectorMath2D.WithComponentAlong(v, direction, speed)</c>.</summary>
    public static void SetSpeedAlong(this IPhysxBody2D body, Vector2 direction, float speed) =>
        body.SetVelocityAlong(direction, speed);

    /// <summary>Carry all speed along the surface <paramref name="tangent"/> (no drifting off a
    /// curve): <c>VectorMath2D.RedirectAlong(v, tangent)</c>.</summary>
    public static void FollowSurface(this IPhysxBody2D body, Vector2 tangent) =>
        body.RedirectVelocityAlong(tangent);

    /// <summary>Press into a surface (grip): <paramref name="acceleration"/> against its
    /// <paramref name="surfaceNormal"/> for one step:
    /// <c>Velocity2D.AccelerateAlong(v, surfaceNormal, -acceleration, dt)</c>, i.e.
    /// <c>v - n * acceleration * dt</c>.</summary>
    public static void StickTo(this IPhysxBody2D body, Vector2 surfaceNormal, float acceleration, float dt) =>
        body.AccelerateAlong(surfaceNormal, -acceleration, dt);

    // ── Speed limits, drag, gravity ───────────────────────────────────────

    /// <summary>Never faster than <paramref name="maxSpeed"/>:
    /// <see cref="BodyMotionExtensions2D.ClampSpeed"/>.</summary>
    public static void ClampTopSpeed(this IPhysxBody2D body, float maxSpeed) => body.ClampSpeed(maxSpeed);

    /// <summary>Air / linear drag for one step: <c>v * (1 - drag * dt)</c>.</summary>
    public static void ApplyDrag(this IPhysxBody2D body, float drag, float dt) => body.ApplyLinearDrag(drag, dt);

    /// <summary>Lose <paramref name="amount"/> of speed, keeping the direction:
    /// <see cref="BodyMotionExtensions2D.ReduceSpeed"/>.</summary>
    public static void BleedSpeed(this IPhysxBody2D body, float amount) => body.ReduceSpeed(amount);

    /// <summary>Fall for one step under <paramref name="gravity"/> scaled by
    /// <paramref name="gravityScale"/> (below 1 floats / hovers, above 1 is heavier):
    /// <c>v.Y - gravity * dt * gravityScale</c>.</summary>
    public static void Fall(this IPhysxBody2D body, float gravity, float dt, float gravityScale = 1f) =>
        body.ApplyGravity(gravity, dt, gravityScale);

    /// <summary>Never come to rest horizontally: keep at least <paramref name="minSpeed"/> along X,
    /// on the current side or <paramref name="fallbackDirection"/> (±1) when still:
    /// <see cref="Velocity2D.KeepMinimumSpeedX"/>.</summary>
    public static void KeepRolling(this IPhysxBody2D body, float minSpeed, int fallbackDirection) =>
        body.KeepMinimumSpeedX(minSpeed, fallbackDirection);

    // ── Orientation ───────────────────────────────────────────────────────

    /// <summary>Turn the body's up toward a surface's <paramref name="normal"/> at
    /// <paramref name="rate"/> (1/s): <see cref="BodyMotionExtensions2D.AlignUpToNormal"/>.</summary>
    public static void AlignToSurface(this IPhysxBody2D body, Vector2 normal, float rate) =>
        body.AlignUpToNormal(normal, rate);

    /// <summary>Turn to <paramref name="targetAngle"/> so it arrives in <paramref name="time"/>
    /// seconds (not sooner than <paramref name="minTime"/>, e.g. one step):
    /// <see cref="BodyMotionExtensions2D.ArriveAtAngle"/>.</summary>
    public static void TurnToAngleIn(this IPhysxBody2D body, float targetAngle, float time, float minTime) =>
        body.ArriveAtAngle(targetAngle, time, minTime);

    /// <summary>Aim at <paramref name="targetAngle"/> like a stick-steered body: a turn rate
    /// proportional to the error (<paramref name="gain"/>), at most <paramref name="maxRate"/>,
    /// reached with at most <paramref name="angularAcceleration"/>:
    /// <see cref="BodyMotionExtensions2D.SteerAngularVelocity"/>.</summary>
    public static void AimAt(this IPhysxBody2D body, float targetAngle, float gain, float maxRate, float angularAcceleration, float dt) =>
        body.SteerAngularVelocity(targetAngle, gain, maxRate, angularAcceleration, dt);

    /// <summary>Slow the spin to zero with at most <paramref name="angularAcceleration"/>:
    /// <c>Scalar.Approach(w, 0, angularAcceleration * dt)</c>.</summary>
    public static void StopSpinning(this IPhysxBody2D body, float angularAcceleration, float dt) =>
        body.ApproachAngularVelocity(0f, angularAcceleration, dt);

    /// <summary>Hold <paramref name="targetAngle"/> with a proportional turn rate:
    /// <c>w = Angle.RateToward(rotation, targetAngle, gain)</c>.</summary>
    public static void HoldAngle(this IPhysxBody2D body, float targetAngle, float gain) =>
        body.RotateTowardAngle(targetAngle, gain);

    /// <summary>The angle at which the body would stand upright on a surface with this
    /// <paramref name="normal"/>, the nearest one to its current rotation (never a full extra turn):
    /// <c>Angle.NearestEquivalent(RotationZ, Rotation2D.AngleAligningUp(normal))</c>.</summary>
    public static float UprightAngleOn(this IPhysxBody2D body, Vector2 normal) =>
        Angle.NearestEquivalent(body.RotationZ, Rotation2D.AngleAligningUp(normal));

    /// <summary>The level (upright, world up) angle nearest to the current rotation, e.g. after a
    /// spin: <c>Angle.NearestFullTurn(RotationZ)</c>.</summary>
    public static float LevelAngle(this IPhysxBody2D body) => Angle.NearestFullTurn(body.RotationZ);

    /// <summary>Snap upright onto a surface: rotate to <see cref="UprightAngleOn"/>, move
    /// <paramref name="sink"/> along -<paramref name="normal"/> (onto it) and stop spinning:
    /// <c>SetTransform(Position - normal * sink, UprightAngleOn(normal)); AngularVelocityZ = 0</c>.</summary>
    public static void SnapUprightOn(this IPhysxBody2D body, Vector2 normal, float sink = 0f)
    {
        var angle = body.UprightAngleOn(normal);
        var p = body.Position - normal * sink;
        body.SetTransform(p, angle);
        body.AngularVelocityZ = 0;
    }

    /// <summary>Turn the body half a turn on the spot (mirror it across its long axis: roof becomes
    /// underside, nose points back), keeping its position and motion:
    /// <c>SetTransform(Position, RotationZ + MathF.PI)</c>.</summary>
    public static void FlipHalfTurn(this IPhysxBody2D body) => body.SetTransform(body.Position, body.RotationZ + MathF.PI);

    /// <summary>Put the body somewhere with a given motion and wake it:
    /// <c>SetTransform(position, angle); LinearVelocity = velocity; AngularVelocityZ = spin; IsAwake = true</c>.</summary>
    public static void ResetMotion(this IPhysxBody2D body, Vector2 position, float angle, Vector2 velocity, float spin) =>
        new BodyState2D(position, angle, velocity, spin).Restore(body);

    // ── Questions (prediction, reach, aim) ────────────────────────────────

    /// <summary>How hard the body hits a surface with this normal (positive = moving into it):
    /// <c>Velocity2D.ApproachSpeed(v, normal)</c>.</summary>
    public static float ImpactSpeedAgainst(this IPhysxBody2D body, Vector2 normal) =>
        Velocity2D.ApproachSpeed(body.LinearVelocity, normal);

    /// <summary>Velocity of a point of the body (its center plus spin):
    /// <see cref="BodyMotionExtensions2D.VelocityAtPoint"/>.</summary>
    public static Vector2 VelocityAt(this IPhysxBody2D body, Vector2 worldPoint) => body.VelocityAtPoint(worldPoint);

    /// <summary>Where a free-flying body will be in <paramref name="t"/> seconds:
    /// <c>Ballistics2D.PositionAt(Position, LinearVelocity, gravity, t)</c>.</summary>
    public static Vector2 PredictPosition(this IPhysxBody2D body, float t, float gravity) =>
        Ballistics2D.PositionAt(body.Position, body.LinearVelocity, gravity, t);

    /// <summary>Where a free-flying body will be after <paramref name="steps"/> steps of
    /// <paramref name="dt"/>, bouncing on a floor at <paramref name="floorY"/>:
    /// <c>Ballistics2D.Predict(Position, LinearVelocity, gravity, dt, steps, floorY, restitution)</c>.</summary>
    public static (Vector2 Position, Vector2 Velocity) PredictLanding(this IPhysxBody2D body, float gravity, float dt, int steps,
                                                                      float floorY, float restitution) =>
        Ballistics2D.Predict(body.Position, body.LinearVelocity, gravity, dt, steps, floorY, restitution);

    /// <summary>How much higher the body still rises in free flight:
    /// <c>Ballistics2D.ApexHeight(LinearVelocity.Y, gravity)</c>.</summary>
    public static float RiseLeft(this IPhysxBody2D body, float gravity) =>
        Ballistics2D.ApexHeight(body.LinearVelocity.Y, gravity);

    /// <summary>Can free flight still take the body <paramref name="height"/> above where it is
    /// (with <paramref name="margin"/> of slack)? <c>!(height &gt; RiseLeft(gravity) + margin)</c>.</summary>
    public static bool CanReachHeight(this IPhysxBody2D body, float height, float gravity, float margin = 0f) =>
        !(height > body.RiseLeft(gravity) + margin);

    /// <summary>Will the body, flying freely from its position with <paramref name="velocity"/> (its
    /// own when null), enter <paramref name="zone"/> (grown by the margins) within
    /// <paramref name="lookahead"/> seconds, checked over <paramref name="steps"/> Euler steps of
    /// <c>lookahead / steps</c>?</summary>
    public static bool WillEnter(this IPhysxBody2D body, Aabb2D zone, float gravity, float lookahead, int steps,
                                 float marginX = 0f, float marginY = 0f, Vector2? velocity = null)
    {
        var h = lookahead / steps;
        return Ballistics2D.FirstStepWhere(body.Position, velocity ?? body.LinearVelocity, gravity, h, steps,
            p => zone.Contains(p.X, p.Y, marginX, marginY)) >= 0;
    }

    /// <summary>Which of <paramref name="zones"/> (goal mouths, danger areas; checked in order) the
    /// free-flying body enters first within <paramref name="steps"/> Euler steps of
    /// <paramref name="dt"/>, and at which step (1-based); <c>(-1, -1)</c> when none, or when it
    /// drops below <paramref name="floorY"/> first:
    /// <c>Ballistics2D.FirstZoneEntered(Position, LinearVelocity, gravity, dt, steps, zones, floorY)</c>.
    /// Margins: pass <c>zone.Grow(marginX, marginY)</c>.</summary>
    public static (int Zone, int Step) PredictZoneEntry(this IPhysxBody2D body, ReadOnlySpan<Aabb2D> zones, float gravity, float dt, int steps,
                                                        float floorY = float.NegativeInfinity) =>
        Ballistics2D.FirstZoneEntered(body.Position, body.LinearVelocity, gravity, dt, steps, zones, floorY);

    /// <summary>The launch velocity that lands on <paramref name="target"/> in exactly
    /// <paramref name="time"/> seconds: <c>Ballistics2D.LaunchVelocity(target - Position, gravity, time)</c>.</summary>
    public static Vector2 VelocityToHit(this IPhysxBody2D body, Vector2 target, float gravity, float time) =>
        Ballistics2D.LaunchVelocity(target - body.Position, gravity, time);

    /// <summary>Seconds to get to <paramref name="target"/> in a straight line, starting from the
    /// current speed toward it (at least 0), accelerating at <paramref name="acceleration"/> up to
    /// <paramref name="maxSpeed"/>: <c>Kinematics.TimeToCover(distance, speedToward, acceleration, maxSpeed)</c>.</summary>
    public static float TimeToReach(this IPhysxBody2D body, Vector2 target, float acceleration, float maxSpeed)
    {
        var dir = VectorMath2D.NormalizeOrZero(target - body.Position, out var distance);
        var v = body.LinearVelocity;
        var toward = MathF.Max(0, v.X * dir.X + v.Y * dir.Y);
        return Kinematics.TimeToCover(distance, toward, acceleration, maxSpeed);
    }

    /// <summary>Distance needed to stop the motion along <paramref name="direction"/> at
    /// <paramref name="deceleration"/>: <c>Kinematics.StoppingDistance(SpeedAlong(direction), deceleration)</c>
    /// (for direction (1, 0) the same bits as <c>v.X * v.X / (2 * deceleration)</c>).</summary>
    public static float BrakingDistanceAlong(this IPhysxBody2D body, Vector2 direction, float deceleration) =>
        Kinematics.StoppingDistance(body.SpeedAlong(direction), deceleration);

    /// <summary>The body's "up" (local +Y) in the world, e.g. the top face of a tilted one-way
    /// platform: <c>GetWorldVector((0, 1))</c>.</summary>
    public static Vector2 UpDirection(this IPhysxBody2D body) => body.GetWorldVector(new Vector2(0, 1));

    /// <summary>How far the world point is above the body's center along the body's up (negative =
    /// below; on a one-way platform: is it over the top face?): <c>GetLocalPoint(worldPoint).Y</c>.</summary>
    public static float HeightAbove(this IPhysxBody2D body, Vector2 worldPoint) => body.GetLocalPoint(worldPoint).Y;

    /// <summary>Is the world point inside the body's box (half extents plus padding)?
    /// <see cref="BodyMotionExtensions2D.BoxContains"/>.</summary>
    public static bool IsWithinBox(this IPhysxBody2D body, Vector2 worldPoint, float halfWidth, float halfHeight,
                                   float padX = 0f, float padY = 0f) =>
        body.BoxContains(worldPoint, halfWidth, halfHeight, padX, padY);

    /// <summary>Is the world point at least <paramref name="gap"/> away from the body's box?
    /// <c>BoxDistanceSquared(p, halfWidth, halfHeight) &gt;= gap * gap</c>.</summary>
    public static bool IsClearOfBox(this IPhysxBody2D body, Vector2 worldPoint, float halfWidth, float halfHeight, float gap) =>
        body.BoxDistanceSquared(worldPoint, halfWidth, halfHeight) >= gap * gap;
}
