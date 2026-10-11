/**
 * Physics layer. Velocity and angular-velocity operations on a body — mirror of C#
 * `Altruist.Physx.TwoD.BodyMotionExtensions2D` (extension methods there, `fn(body, ...)` here).
 * Each reads the body, applies one math-layer / Velocity2D formula and writes once.
 * Speeds are measured with `Math.sqrt(x * x + y * y)` like C# (not `Math.hypot`, which can differ in
 * the last bit).
 *
 * Which layer: use these when you hold a body and want a mechanical velocity change; use the math
 * layer (`VectorMath2D`, `Velocity2D`) on velocity values that are not a body's (prediction,
 * captured incoming velocity); prefer the intent-named `GameplayVerbs2D` in gameplay code.
 * Side effects: each write goes through planck's `setLinearVelocity` / `setAngularVelocity`, which
 * wake a sleeping body when the new value is non-zero and are ignored by static bodies; functions
 * documented as "written only when ..." skip the write (and the wake) otherwise. Angles in radians,
 * counter-clockwise; `dt` in seconds.
 */
import type { NormalFrame2D } from '../math/normalFrame2D.ts';
import { arriveRate, clampedRateToward, rateToward, wrap } from '../math/angle.ts';
import { boxContainsLocal, boxDistanceSquaredLocal } from '../math/geometry2D.ts';
import { angleAligningUp } from '../math/rotation2D.ts';
import { approach, lerp as lerpScalar } from '../math/scalar.ts';
import type { Vec2Like } from '../math/vec2.ts';
import {
  addAlong,
  approachComponentAlong,
  clampLength,
  lerp,
  redirectAlong,
  shortenBy,
  withComponentAlong,
} from '../math/vectorMath2D.ts';
import type { AngularBody, Body2DLike, LinearBody } from './body2D.ts';
import {
  accelerateAlong as accelerate,
  applyGravity as gravityStep,
  applyLinearDrag as dragStep,
  bounce,
  cancelInto,
  keepMinimumSpeedX as keepMinX,
  pointVelocity,
} from './velocity2D.ts';

// ── Read ──────────────────────────────────────────────────────────────

/** Velocity component along the unit `direction`: `v.x * d.x + v.y * d.y`. Read-only. Mirrors C#
 * `BodyMotionExtensions2D.SpeedAlong`. */
export function speedAlong(body: LinearBody, direction: Vec2Like): number {
  const v = body.getLinearVelocity();
  return v.x * direction.x + v.y * direction.y;
}

/** Velocity of a world point of the body (linear plus spin):
 * `pointVelocity(v, w, worldPoint - worldCenter)`. Read-only. Mirrors C#
 * `BodyMotionExtensions2D.VelocityAtPoint`; verb {@link GameplayVerbs2D.velocityAt}. */
export function velocityAtPoint(body: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>, worldPoint: Vec2Like): Vec2Like {
  const c = body.getWorldCenter();
  return pointVelocity(body.getLinearVelocity(), body.getAngularVelocity(), { x: worldPoint.x - c.x, y: worldPoint.y - c.y });
}

/** Whether the world point is inside the body's centered box (half extents plus padding):
 * `boxContainsLocal(getLocalPoint(p), halfWidth + padX, halfHeight + padY)`. Read-only. Mirrors C#
 * `BodyMotionExtensions2D.BoxContains`; math form {@link Geometry2D.boxContainsLocal}. */
export function boxContains(
  body: Pick<Body2DLike, 'getLocalPoint'>,
  worldPoint: Vec2Like,
  halfWidth: number,
  halfHeight: number,
  padX = 0,
  padY = 0,
): boolean {
  return boxContainsLocal(body.getLocalPoint(worldPoint), halfWidth + padX, halfHeight + padY);
}

/** Squared distance from the world point to the body's centered box (0 inside):
 * `boxDistanceSquaredLocal(getLocalPoint(p), halfWidth, halfHeight)`. Read-only. Mirrors C#
 * `BodyMotionExtensions2D.BoxDistanceSquared`. */
export function boxDistanceSquared(body: Pick<Body2DLike, 'getLocalPoint'>, worldPoint: Vec2Like, halfWidth: number, halfHeight: number): number {
  return boxDistanceSquaredLocal(body.getLocalPoint(worldPoint), halfWidth, halfHeight);
}

// ── Linear velocity ───────────────────────────────────────────────────

/**
 * Adds `amount` along the unit `direction`, keeping the rest of the motion:
 * `v = addAlong(v, direction, amount)`. Writes once via `setLinearVelocity` (planck wakes the body when the result is non-zero).
 *
 * Alternatives: {@link setVelocityAlong} sets that component to an exact value;
 * {@link VectorMath2D.addAlong} is the pure form; the gameplay verb is
 * {@link GameplayVerbs2D.pushAlong}, while {@link GameplayVerbs2D.launchAlong} replaces the whole
 * velocity. Mirrors C# `BodyMotionExtensions2D.AddVelocityAlong`.
 */
export function addVelocityAlong(body: LinearBody, direction: Vec2Like, amount: number): void {
  body.setLinearVelocity(addAlong(body.getLinearVelocity(), direction, amount));
}

/** Sets the component along the unit `direction` to exactly `speed`, keeping the perpendicular part:
 * `v = withComponentAlong(v, direction, speed)`. Writes once via `setLinearVelocity` (planck wakes the body when the result is non-zero). To ease toward a target speed use
 * {@link approachVelocityAlong}. Verb: {@link GameplayVerbs2D.setSpeedAlong}. Mirrors C#
 * `BodyMotionExtensions2D.SetVelocityAlong`. */
export function setVelocityAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  body.setLinearVelocity(withComponentAlong(body.getLinearVelocity(), direction, speed));
}

/** Moves the component along the unit `direction` toward `target` by at most `acceleration * dt`
 * (units/s² times seconds): `v = approachComponentAlong(v, direction, target, acceleration * dt)`.
 * Verbs: {@link GameplayVerbs2D.driveAlong}, {@link GameplayVerbs2D.brakeAlong}. Mirrors C#
 * `BodyMotionExtensions2D.ApproachVelocityAlong`. */
export function approachVelocityAlong(body: LinearBody, direction: Vec2Like, target: number, acceleration: number, dt: number): void {
  body.setLinearVelocity(approachComponentAlong(body.getLinearVelocity(), direction, target, acceleration * dt));
}

/** Re-points the whole velocity along `axis`, keeping its speed (e.g. follow a surface tangent):
 * `v = redirectAlong(v, axis)`. Writes once via `setLinearVelocity` (planck wakes the body when the result is non-zero). Verb: {@link GameplayVerbs2D.followSurface}. Mirrors C#
 * `BodyMotionExtensions2D.RedirectVelocityAlong`. */
export function redirectVelocityAlong(body: LinearBody, axis: Vec2Like): void {
  body.setLinearVelocity(redirectAlong(body.getLinearVelocity(), axis));
}

/** Constant acceleration along a direction for one step (no target, no cap):
 * `v = Velocity2D.accelerateAlong(v, direction, acceleration, dt)`. Verb: {@link GameplayVerbs2D.stickTo}
 * (negative acceleration along the normal). Mirrors C# `BodyMotionExtensions2D.AccelerateAlong`. */
export function accelerateAlong(body: LinearBody, direction: Vec2Like, acceleration: number, dt: number): void {
  body.setLinearVelocity(accelerate(body.getLinearVelocity(), direction, acceleration, dt));
}

/** Removes `amount` (1 = all) of the motion into the unit `normal`:
 * `v = Velocity2D.cancelInto(v, normal, amount)`. Always writes (also when unchanged). Verb:
 * {@link GameplayVerbs2D.cancelMotionAgainst}. Mirrors C# `BodyMotionExtensions2D.CancelVelocityInto`. */
export function cancelVelocityInto(body: LinearBody, normal: Vec2Like, amount = 1): void {
  body.setLinearVelocity(cancelInto(body.getLinearVelocity(), normal, amount));
}

/** Bounces the body's current velocity: `v = Velocity2D.bounce(v, normal, normalSpeed, tangentKeep)`.
 * To bounce from a velocity recorded earlier (e.g. in pre-solve, before the solver changed it) use
 * {@link GameplayVerbs2D.bounceOff} with `incoming`, or {@link Velocity2D.bounce} directly. Mirrors
 * C# `BodyMotionExtensions2D.BounceVelocity`. */
export function bounceVelocity(body: LinearBody, normal: Vec2Like, normalSpeed: number, tangentKeep: number): void {
  body.setLinearVelocity(bounce(body.getLinearVelocity(), normal, normalSpeed, tangentKeep));
}

/** Shortens the velocity by `amount` (speed units): `speed = |v|; shortenBy(v, speed, amount)`
 * (no write at rest; overshoots and reverses if `amount > speed`). Verb: {@link GameplayVerbs2D.bleedSpeed}.
 * Mirrors C# `BodyMotionExtensions2D.ReduceSpeed`. */
export function reduceSpeed(body: LinearBody, amount: number): void {
  const v = body.getLinearVelocity();
  const speed = Math.sqrt(v.x * v.x + v.y * v.y);
  if (speed === 0) return;
  body.setLinearVelocity(shortenBy(v, speed, amount));
}

/** Caps the speed at `maxSpeed`: when `s = |v|` exceeds it, `v = clampLength(v, maxSpeed)`
 * (`v.x / s * maxSpeed`). Written only when faster. Verb: {@link GameplayVerbs2D.clampTopSpeed}.
 * Mirrors C# `BodyMotionExtensions2D.ClampSpeed` (which measures with `Vector2.Length`). */
export function clampSpeed(body: LinearBody, maxSpeed: number): void {
  const v = body.getLinearVelocity();
  const s = Math.sqrt(v.x * v.x + v.y * v.y);
  if (s > maxSpeed) body.setLinearVelocity(clampLength(v, maxSpeed));
}

/** Hand-applied linear drag for one step: `v = Velocity2D.applyLinearDrag(v, drag, dt)`. For
 * engine-side damping use the body's linear damping instead. Verb: {@link GameplayVerbs2D.applyDrag}.
 * Mirrors C# `BodyMotionExtensions2D.ApplyLinearDrag`. */
export function applyLinearDrag(body: LinearBody, drag: number, dt: number): void {
  body.setLinearVelocity(dragStep(body.getLinearVelocity(), drag, dt));
}

/** Hand-applied gravity for one step (for worlds with zero engine gravity):
 * `v = Velocity2D.applyGravity(v, gravity, dt, scale)` (+Y up, `gravity` ≥ 0 pulls toward -Y).
 * Verb: {@link GameplayVerbs2D.fall}. Mirrors C# `BodyMotionExtensions2D.ApplyGravity`. */
export function applyGravity(body: LinearBody, gravity: number, dt: number, scale = 1): void {
  body.setLinearVelocity(gravityStep(body.getLinearVelocity(), gravity, dt, scale));
}

/** `Velocity2D.keepMinimumSpeedX(v, minSpeed, fallbackDirection)`, written only when it changes the
 * velocity. Verb: {@link GameplayVerbs2D.keepRolling}. Mirrors C# `BodyMotionExtensions2D.KeepMinimumSpeedX`. */
export function keepMinimumSpeedX(body: LinearBody, minSpeed: number, fallbackDirection: number): void {
  const v = body.getLinearVelocity();
  if (!(Math.abs(v.x) < minSpeed)) return;
  body.setLinearVelocity(keepMinX(v, minSpeed, fallbackDirection));
}

/** Keeps only `keep` (0..1) of the velocity change since `(linearBefore, angularBefore)`:
 * `v = before + (v - before) * keep; w = wBefore + (w - wBefore) * keep` (two writes). Verb:
 * {@link GameplayVerbs2D.absorbImpact}. Mirrors C# `BodyMotionExtensions2D.BlendMotionFrom`. */
export function blendMotionFrom(body: LinearBody & AngularBody, linearBefore: Vec2Like, angularBefore: number, keep: number): void {
  body.setLinearVelocity(lerp(linearBefore, body.getLinearVelocity(), keep));
  body.setAngularVelocity(lerpScalar(angularBefore, body.getAngularVelocity(), keep));
}

/** Sets the velocity from its parts in a contact / surface frame: `v = frame.compose(alongNormal, alongTangent)`.
 * Mirrors C# `BodyMotionExtensions2D.SetVelocityInFrame`. */
export function setVelocityInFrame(body: LinearBody, frame: NormalFrame2D, alongNormal: number, alongTangent: number): void {
  body.setLinearVelocity(frame.compose(alongNormal, alongTangent));
}

// ── Angular velocity ──────────────────────────────────────────────────

/** Turns the body's up (local +Y) toward `normal` by setting the angular velocity:
 * `w = wrap(angleAligningUp(normal) - angle) * rate` (`rate` in 1/s). Writes via
 * `setAngularVelocity` (planck wakes on non-zero). Verb: {@link GameplayVerbs2D.alignToSurface}.
 * Mirrors C# `BodyMotionExtensions2D.AlignUpToNormal`. */
export function alignUpToNormal(body: AngularBody, normal: Vec2Like, rate: number): void {
  body.setAngularVelocity(wrap(angleAligningUp(normal) - body.getAngle()) * rate);
}

/** Sets the angular velocity that reaches `targetAngle` in `time` seconds (at least `minTime`):
 * `w = Angle.arriveRate(angle, targetAngle, time, minTime)`. Verb: {@link GameplayVerbs2D.turnToAngleIn}.
 * Mirrors C# `BodyMotionExtensions2D.ArriveAtAngle`. */
export function arriveAtAngle(body: AngularBody, targetAngle: number, time: number, minTime: number): void {
  body.setAngularVelocity(arriveRate(body.getAngle(), targetAngle, time, minTime));
}

/** Proportional turn: `w = Angle.rateToward(angle, targetAngle, gain)` (unclamped). Verb:
 * {@link GameplayVerbs2D.holdAngle}; with limits use {@link steerAngularVelocity}. Mirrors C#
 * `BodyMotionExtensions2D.RotateTowardAngle`. */
export function rotateTowardAngle(body: AngularBody, targetAngle: number, gain: number): void {
  body.setAngularVelocity(rateToward(body.getAngle(), targetAngle, gain));
}

/** Moves the angular velocity toward `targetRate` by at most `angularAcceleration * dt`:
 * `w = approach(w, targetRate, angularAcceleration * dt)`. Verb: {@link GameplayVerbs2D.stopSpinning}
 * (target 0). Mirrors C# `BodyMotionExtensions2D.ApproachAngularVelocity`. */
export function approachAngularVelocity(body: AngularBody, targetRate: number, angularAcceleration: number, dt: number): void {
  body.setAngularVelocity(approach(body.getAngularVelocity(), targetRate, angularAcceleration * dt));
}

/** Clamped proportional steering with an acceleration limit:
 * `target = clampedRateToward(angle, targetAngle, gain, maxRate); w = approach(w, target, angularAcceleration * dt)`.
 * Verb: {@link GameplayVerbs2D.aimAt}. Mirrors C# `BodyMotionExtensions2D.SteerAngularVelocity`. */
export function steerAngularVelocity(
  body: AngularBody,
  targetAngle: number,
  gain: number,
  maxRate: number,
  angularAcceleration: number,
  dt: number,
): void {
  const w = clampedRateToward(body.getAngle(), targetAngle, gain, maxRate);
  body.setAngularVelocity(approach(body.getAngularVelocity(), w, angularAcceleration * dt));
}

/** Adds the spin a surface slip (tangential speed difference, e.g. {@link ContactImpact2D.slip})
 * gives a round body of `radius`: `w = w + (slip / radius) * factor`. Mirrors C#
 * `BodyMotionExtensions2D.AddSpinFromSlip`. */
export function addSpinFromSlip(body: AngularBody, slip: number, radius: number, factor: number): void {
  body.setAngularVelocity(body.getAngularVelocity() + (slip / radius) * factor);
}

// ── State ─────────────────────────────────────────────────────────────

/** Copies `source`'s transform and velocities onto `body` (copies of the vectors), then wakes it:
 * `setTransform; setLinearVelocity; setAngularVelocity; setAwake(true)`. For a stored snapshot use
 * {@link BodyState2D.capture} / {@link BodyState2D.restore}. Mirrors C# `BodyMotionExtensions2D.CopyMotionFrom`. */
export function copyMotionFrom(body: Body2DLike, source: Body2DLike): void {
  const p = source.getPosition();
  body.setTransform({ x: p.x, y: p.y }, source.getAngle());
  const v = source.getLinearVelocity();
  body.setLinearVelocity({ x: v.x, y: v.y });
  body.setAngularVelocity(source.getAngularVelocity());
  body.setAwake(true);
}
