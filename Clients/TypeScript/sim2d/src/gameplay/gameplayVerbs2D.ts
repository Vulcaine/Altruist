/**
 * Gameplay layer. Designer-level actions and questions on a body — mirror of C#
 * `Altruist.Gaming.TwoD.GameplayVerbs2D` (`body.JumpOff(n, s)` there, `jumpOff(body, n, s)` here).
 * Each verb delegates to one physics-layer operation (which delegates to the math layer) with the
 * same operation order, so it gives the same numbers as the hand-written update it replaces.
 * Side view, +Y up, `gravity` = magnitude of the pull toward -Y; counter-clockwise angles.
 * Speeds are measured with `Math.sqrt(x * x + y * y)` like C# (not `Math.hypot`).
 *
 * Layers: the math layer (`VectorMath2D`, `Velocity2D`) computes values; the physics layer
 * (`BodyMotion2D`) applies one formula to a body; these verbs name the intent and are the preferred
 * entry point in gameplay code. Writes go through planck's `setLinearVelocity` /
 * `setAngularVelocity`, which wake a sleeping body on a non-zero value.
 */
import { nearestEquivalent, nearestFullTurn } from '../math/angle.ts';
import { contains, type Aabb2DLike } from '../math/aabb2D.ts';
import { clampMinMax } from '../math/scalar.ts';
import { angleAligningUp, upAt } from '../math/rotation2D.ts';
import type { Vec2Like } from '../math/vec2.ts';
import { addAlong, normalizeOrZeroWithLength } from '../math/vectorMath2D.ts';
import { apexHeight, firstStepWhere, firstZoneEntered, launchVelocity, positionAt, predict, type FlightState } from '../physics/ballistics2D.ts';
import type { AngularBody, Body2DLike, LinearBody, PositionedBody } from '../physics/body2D.ts';
import * as motion from '../physics/bodyMotion2D.ts';
import { restore } from '../physics/bodyState2D.ts';
import { stoppingDistance, timeToCover } from '../physics/kinematics.ts';
import { approachSpeed, bounce, cancelInto } from '../physics/velocity2D.ts';

// ── Launching and jumping ─────────────────────────────────────────────

/** Jump off a surface: cancel any motion into it, then add `speed` straight out along its unit
 * `surfaceNormal` (any normal works, e.g. the body's own up for an air jump):
 * `v = cancelInto(v, n); v = addAlong(v, n, speed)` (one write). Mirrors C# `GameplayVerbs2D.JumpOff`. */
export function jumpOff(body: LinearBody, surfaceNormal: Vec2Like, speed: number): void {
  const v = cancelInto(body.getLinearVelocity(), surfaceNormal);
  body.setLinearVelocity(addAlong(v, surfaceNormal, speed));
}

/**
 * Jump as if the body already faced where it is being aimed: `jumpOff(body, upAt(aimedRotation), speed)`.
 * For fast play, where the jump comes before a rate-limited turn has finished: the jump follows the
 * player's aim (e.g. the stick) instead of the half-turned body, so "aim, then jump" acts on the aim.
 * `aimedRotation` in radians, CCW (body convention, see {@link Rotation2D.upAt}). Mirrors C#
 * `GameplayVerbs2D.JumpAsAimed`.
 */
export function jumpAsAimed(body: LinearBody, aimedRotation: number, speed: number): void {
  jumpOff(body, upAt(aimedRotation), speed);
}

/** Add `speed` along the unit `direction`, keeping the current motion: {@link BodyMotion2D.addVelocityAlong}.
 *
 * Pick between the three: `pushAlong` adds to the component along `direction`;
 * {@link setSpeedAlong} sets that component to an exact value (perpendicular part kept);
 * {@link launchAlong} discards the current motion entirely. Mirrors C# `GameplayVerbs2D.PushAlong`. */
export function pushAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  motion.addVelocityAlong(body, direction, speed);
}

/** Replace the whole motion with `speed` along the unit `direction`: `v = (d.x * speed, d.y * speed)`.
 * Compare {@link pushAlong} (adds) and {@link setSpeedAlong} (one component). Mirrors C#
 * `GameplayVerbs2D.LaunchAlong`. */
export function launchAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  body.setLinearVelocity({ x: direction.x * speed, y: direction.y * speed });
}

/** {@link launchAlong} the body's own `localForward` axis (e.g. `(1, 0)`) turned into world space.
 * Mirrors C# `GameplayVerbs2D.LaunchForward`. */
export function launchForward(body: LinearBody & Pick<Body2DLike, 'getWorldVector'>, localForward: Vec2Like, speed: number): void {
  launchAlong(body, body.getWorldVector(localForward), speed);
}

/** Cancel `fraction` (1 = all) of the motion that goes against `direction` (e.g. before a thrust
 * along it): {@link BodyMotion2D.cancelVelocityInto}. Mirrors C# `GameplayVerbs2D.CancelMotionAgainst`. */
export function cancelMotionAgainst(body: LinearBody, direction: Vec2Like, fraction = 1): void {
  motion.cancelVelocityInto(body, direction, fraction);
}

/** Bounce / launch off a surface: `outSpeed` along the normal plus `tangentKeep` of the motion
 * along it, from `incoming` (a recorded velocity, e.g. captured in pre-solve before the solver) or
 * the current velocity: `v = Velocity2D.bounce(incoming ?? v, normal, outSpeed, tangentKeep)`.
 * Mirrors both C# `GameplayVerbs2D.BounceOff` overloads. Pure-value form: {@link Velocity2D.bounce};
 * body form without `incoming`: {@link BodyMotion2D.bounceVelocity}. */
export function bounceOff(body: LinearBody, normal: Vec2Like, outSpeed: number, tangentKeep: number, incoming?: Vec2Like): void {
  body.setLinearVelocity(bounce(incoming ?? body.getLinearVelocity(), normal, outSpeed, tangentKeep));
}

/** Keep only `keep` (recoil, 0..1) of the velocity change a hit caused since
 * `(velocityBefore, spinBefore)`: {@link BodyMotion2D.blendMotionFrom}. Mirrors C# `GameplayVerbs2D.AbsorbImpact`. */
export function absorbImpact(body: LinearBody & AngularBody, velocityBefore: Vec2Like, spinBefore: number, keep: number): void {
  motion.blendMotionFrom(body, velocityBefore, spinBefore, keep);
}

// ── Horizontal / vertical motion ──────────────────────────────────────
// One component written directly, the other kept (not setSpeedAlong on an axis, which rounds differently).

/** Set the horizontal speed, keeping the vertical: `v = (x, v.y)`. Mirrors C# `GameplayVerbs2D.SetVelocityX`. */
export function setVelocityX(body: LinearBody, x: number): void {
  body.setLinearVelocity({ x, y: body.getLinearVelocity().y });
}

/** Set the vertical speed, keeping the horizontal: `v = (v.x, y)`. Mirrors C# `GameplayVerbs2D.SetVelocityY`. */
export function setVelocityY(body: LinearBody, y: number): void {
  body.setLinearVelocity({ x: body.getLinearVelocity().x, y });
}

/** Scale the horizontal speed: `v = (v.x * factor, v.y)`. Mirrors C# `GameplayVerbs2D.ScaleVelocityX`. */
export function scaleVelocityX(body: LinearBody, factor: number): void {
  const v = body.getLinearVelocity();
  body.setLinearVelocity({ x: v.x * factor, y: v.y });
}

/** Scale the vertical speed: `v = (v.x, v.y * factor)`. Mirrors C# `GameplayVerbs2D.ScaleVelocityY`. */
export function scaleVelocityY(body: LinearBody, factor: number): void {
  const v = body.getLinearVelocity();
  body.setLinearVelocity({ x: v.x, y: v.y * factor });
}

/** Keep the vertical speed within [min, max]: `v = (v.x, clampMinMax(v.y, min, max))` =
 * `Math.min(Math.max(v.y, min), max)` (`max` wins if min > max). Mirrors C# `GameplayVerbs2D.ClampVelocityY`. */
export function clampVelocityY(body: LinearBody, min: number, max: number): void {
  const v = body.getLinearVelocity();
  body.setLinearVelocity({ x: v.x, y: clampMinMax(v.y, min, max) });
}

// ── Driving on surfaces ───────────────────────────────────────────────

/** Speed up or slow down along the unit `tangent` toward `targetSpeed` by at most `acceleration`
 * per second, leaving the motion across it alone: {@link BodyMotion2D.approachVelocityAlong}. Mirrors
 * C# `GameplayVerbs2D.DriveAlong`. */
export function driveAlong(body: LinearBody, tangent: Vec2Like, targetSpeed: number, acceleration: number, dt: number): void {
  motion.approachVelocityAlong(body, tangent, targetSpeed, acceleration, dt);
}

/** Brake toward a stop along `direction`: `driveAlong(body, direction, 0, deceleration, dt)`.
 * Mirrors C# `GameplayVerbs2D.BrakeAlong`. */
export function brakeAlong(body: LinearBody, direction: Vec2Like, deceleration: number, dt: number): void {
  motion.approachVelocityAlong(body, direction, 0, deceleration, dt);
}

/** Set the speed along the unit `direction` to exactly `speed`, keeping the perpendicular part:
 * {@link BodyMotion2D.setVelocityAlong}. Compare {@link pushAlong} (adds) and {@link launchAlong}
 * (replaces everything). Mirrors C# `GameplayVerbs2D.SetSpeedAlong`. */
export function setSpeedAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  motion.setVelocityAlong(body, direction, speed);
}

/** Carry all speed along the surface `tangent` (no drifting off a curve): {@link BodyMotion2D.redirectVelocityAlong}.
 * Mirrors C# `GameplayVerbs2D.FollowSurface`. */
export function followSurface(body: LinearBody, tangent: Vec2Like): void {
  motion.redirectVelocityAlong(body, tangent);
}

/** Press into a surface (grip) for one step: `v - n * acceleration * dt`, via
 * {@link BodyMotion2D.accelerateAlong} with `-acceleration`. Mirrors C# `GameplayVerbs2D.StickTo`. */
export function stickTo(body: LinearBody, surfaceNormal: Vec2Like, acceleration: number, dt: number): void {
  motion.accelerateAlong(body, surfaceNormal, -acceleration, dt);
}

// ── Speed limits, drag, gravity ───────────────────────────────────────

/** Never faster than `maxSpeed`: {@link BodyMotion2D.clampSpeed} (written only when faster). Mirrors
 * C# `GameplayVerbs2D.ClampTopSpeed`. */
export function clampTopSpeed(body: LinearBody, maxSpeed: number): void {
  motion.clampSpeed(body, maxSpeed);
}

/** Air / linear drag for one step: `v * (1 - drag * dt)` ({@link BodyMotion2D.applyLinearDrag}).
 * Mirrors C# `GameplayVerbs2D.ApplyDrag`. */
export function applyDrag(body: LinearBody, drag: number, dt: number): void {
  motion.applyLinearDrag(body, drag, dt);
}

/** Lose `amount` of speed, keeping the direction: {@link BodyMotion2D.reduceSpeed}. Mirrors C#
 * `GameplayVerbs2D.BleedSpeed`. */
export function bleedSpeed(body: LinearBody, amount: number): void {
  motion.reduceSpeed(body, amount);
}

/** Fall for one step under hand-applied gravity (for worlds with zero engine gravity):
 * `v.y - gravity * dt * gravityScale` (below 1 floats, above 1 is heavier). Mirrors C# `GameplayVerbs2D.Fall`. */
export function fall(body: LinearBody, gravity: number, dt: number, gravityScale = 1): void {
  motion.applyGravity(body, gravity, dt, gravityScale);
}

/** Never come to rest horizontally: keep at least `minSpeed` along X, on the current side or
 * `fallbackDirection` (±1) when still ({@link BodyMotion2D.keepMinimumSpeedX}). Mirrors C#
 * `GameplayVerbs2D.KeepRolling`. */
export function keepRolling(body: LinearBody, minSpeed: number, fallbackDirection: number): void {
  motion.keepMinimumSpeedX(body, minSpeed, fallbackDirection);
}

// ── Orientation ───────────────────────────────────────────────────────

/** Turn the body's up toward a surface's `normal` at `rate` (1/s): {@link BodyMotion2D.alignUpToNormal}.
 * Mirrors C# `GameplayVerbs2D.AlignToSurface`. */
export function alignToSurface(body: AngularBody, normal: Vec2Like, rate: number): void {
  motion.alignUpToNormal(body, normal, rate);
}

/** Turn to `targetAngle` so it arrives in `time` seconds (not sooner than `minTime`, e.g. one step):
 * {@link BodyMotion2D.arriveAtAngle}. Mirrors C# `GameplayVerbs2D.TurnToAngleIn`. */
export function turnToAngleIn(body: AngularBody, targetAngle: number, time: number, minTime: number): void {
  motion.arriveAtAngle(body, targetAngle, time, minTime);
}

/** Aim at `targetAngle` like a stick-steered body: turn rate proportional to the error (`gain`), at
 * most `maxRate`, reached with at most `angularAcceleration`: {@link BodyMotion2D.steerAngularVelocity}.
 * Mirrors C# `GameplayVerbs2D.AimAt`. */
export function aimAt(body: AngularBody, targetAngle: number, gain: number, maxRate: number, angularAcceleration: number, dt: number): void {
  motion.steerAngularVelocity(body, targetAngle, gain, maxRate, angularAcceleration, dt);
}

/** Slow the spin to zero with at most `angularAcceleration`: `w = approach(w, 0, angularAcceleration * dt)`.
 * Mirrors C# `GameplayVerbs2D.StopSpinning`. */
export function stopSpinning(body: AngularBody, angularAcceleration: number, dt: number): void {
  motion.approachAngularVelocity(body, 0, angularAcceleration, dt);
}

/** Hold `targetAngle` with a proportional (unclamped) turn rate: `w = Angle.rateToward(angle, targetAngle, gain)`.
 * Mirrors C# `GameplayVerbs2D.HoldAngle`. */
export function holdAngle(body: AngularBody, targetAngle: number, gain: number): void {
  motion.rotateTowardAngle(body, targetAngle, gain);
}

/** The angle at which the body would stand upright on a surface with this `normal`, nearest to its
 * current (unwrapped) rotation: `nearestEquivalent(angle, angleAligningUp(normal))`. Read-only.
 * Mirrors C# `GameplayVerbs2D.UprightAngleOn`. */
export function uprightAngleOn(body: Pick<AngularBody, 'getAngle'>, normal: Vec2Like): number {
  return nearestEquivalent(body.getAngle(), angleAligningUp(normal));
}

/** The level (world-up) angle nearest to the current rotation, e.g. after a spin: `nearestFullTurn(angle)`.
 * Read-only. Mirrors C# `GameplayVerbs2D.LevelAngle`. */
export function levelAngle(body: Pick<AngularBody, 'getAngle'>): number {
  return nearestFullTurn(body.getAngle());
}

/** Snap upright onto a surface: rotate to {@link uprightAngleOn}, move `sink` along -`normal` and
 * stop spinning: `setTransform(position - normal * sink, uprightAngleOn(normal)); w = 0`. Mirrors C#
 * `GameplayVerbs2D.SnapUprightOn`. */
export function snapUprightOn(body: Body2DLike, normal: Vec2Like, sink = 0): void {
  const angle = uprightAngleOn(body, normal);
  const p = body.getPosition();
  body.setTransform({ x: p.x - normal.x * sink, y: p.y - normal.y * sink }, angle);
  body.setAngularVelocity(0);
}

/** Half a turn on the spot (roof becomes underside), keeping position and motion:
 * `setTransform(position, angle + Math.PI)` (angle not wrapped). Mirrors C# `GameplayVerbs2D.FlipHalfTurn`. */
export function flipHalfTurn(body: Body2DLike): void {
  body.setTransform(body.getPosition(), body.getAngle() + Math.PI);
}

/** Place the body with a given motion and wake it:
 * `setTransform(position, angle); v = velocity; w = spin; setAwake(true)` (via {@link BodyState2D.restore}).
 * Mirrors C# `GameplayVerbs2D.ResetMotion`. */
export function resetMotion(body: Body2DLike, position: Vec2Like, angle: number, velocity: Vec2Like, spin: number): void {
  restore({ position, angle, linearVelocity: velocity, angularVelocity: spin, isAwake: true, isActive: true }, body);
}

// ── Questions ─────────────────────────────────────────────────────────

/** How hard the body hits a surface with this `normal` (positive = moving into it):
 * `Velocity2D.approachSpeed(v, normal)`. Mirrors C# `GameplayVerbs2D.ImpactSpeedAgainst`. */
export function impactSpeedAgainst(body: LinearBody, normal: Vec2Like): number {
  return approachSpeed(body.getLinearVelocity(), normal);
}

/** Velocity of a world point of the body (center plus spin): {@link BodyMotion2D.velocityAtPoint}.
 * Mirrors C# `GameplayVerbs2D.VelocityAt`. */
export function velocityAt(body: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>, worldPoint: Vec2Like): Vec2Like {
  return motion.velocityAtPoint(body, worldPoint);
}

/** Where a free-flying body will be in `t` seconds (closed form):
 * `Ballistics2D.positionAt(position, v, gravity, t)`. Mirrors C# `GameplayVerbs2D.PredictPosition`. */
export function predictPosition(body: LinearBody & PositionedBody, t: number, gravity: number): Vec2Like {
  return positionAt(body.getPosition(), body.getLinearVelocity(), gravity, t);
}

/** State of a free-flying body after `steps` Euler steps of `dt`, bouncing on a floor at `floorY`:
 * `Ballistics2D.predict(position, v, gravity, dt, steps, floorY, restitution)`. Mirrors C#
 * `GameplayVerbs2D.PredictLanding`. */
export function predictLanding(
  body: LinearBody & PositionedBody,
  gravity: number,
  dt: number,
  steps: number,
  floorY: number,
  restitution: number,
): FlightState {
  return predict(body.getPosition(), body.getLinearVelocity(), gravity, dt, steps, floorY, restitution);
}

/** How much higher the body still rises in free flight: `Ballistics2D.apexHeight(v.y, gravity)`.
 * Mirrors C# `GameplayVerbs2D.RiseLeft`. */
export function riseLeft(body: LinearBody, gravity: number): number {
  return apexHeight(body.getLinearVelocity().y, gravity);
}

/** Can free flight still take the body `height` above where it is (with `margin` of slack)?
 * `!(height > riseLeft(gravity) + margin)`. Mirrors C# `GameplayVerbs2D.CanReachHeight`. */
export function canReachHeight(body: LinearBody, height: number, gravity: number, margin = 0): boolean {
  return !(height > riseLeft(body, gravity) + margin);
}

/** Will free flight (own velocity, or `velocity`) enter `zone` (with margins) within `lookahead` s
 * over `steps` Euler steps of `lookahead / steps`? Margins grow the zone as in
 * {@link Aabb2D.contains}. Mirrors C# `GameplayVerbs2D.WillEnter`. */
export function willEnter(
  body: LinearBody & PositionedBody,
  zone: Aabb2DLike,
  gravity: number,
  lookahead: number,
  steps: number,
  marginX = 0,
  marginY = 0,
  velocity?: Vec2Like,
): boolean {
  const h = lookahead / steps;
  return firstStepWhere(body.getPosition(), velocity ?? body.getLinearVelocity(), gravity, h, steps, (p) => contains(zone, p.x, p.y, marginX, marginY)) >= 0;
}

/** The first of `zones` (in order) free flight enters within `steps` steps of `dt`, and the 1-based
 * step; `{ zone: -1, step: -1 }` when none or below `floorY` first:
 * `firstZoneEntered(position, velocity, gravity, dt, steps, zones, floorY)`. Margins: pass
 * `Aabb2D.grow(zone, mx, my)`. Mirrors C# `GameplayVerbs2D.PredictZoneEntry`. */
export function predictZoneEntry(
  body: LinearBody & PositionedBody,
  zones: readonly Aabb2DLike[],
  gravity: number,
  dt: number,
  steps: number,
  floorY = -Infinity,
): { zone: number; step: number } {
  return firstZoneEntered(body.getPosition(), body.getLinearVelocity(), gravity, dt, steps, zones, floorY);
}

/** The launch velocity that lands on `target` in exactly `time` seconds:
 * `Ballistics2D.launchVelocity(target - position, gravity, time)`. Read-only (does not set it).
 * Mirrors C# `GameplayVerbs2D.VelocityToHit`. */
export function velocityToHit(body: PositionedBody, target: Vec2Like, gravity: number, time: number): Vec2Like {
  const p = body.getPosition();
  return launchVelocity({ x: target.x - p.x, y: target.y - p.y }, gravity, time);
}

/** Seconds to reach `target` in a straight line, starting from the current speed toward it (at
 * least 0), accelerating at `acceleration` up to `maxSpeed`:
 * `Kinematics.timeToCover(distance, speedToward, acceleration, maxSpeed)`. Mirrors C#
 * `GameplayVerbs2D.TimeToReach`. */
export function timeToReach(body: LinearBody & PositionedBody, target: Vec2Like, acceleration: number, maxSpeed: number): number {
  const p = body.getPosition();
  const { direction: dir, length: distance } = normalizeOrZeroWithLength({ x: target.x - p.x, y: target.y - p.y });
  const v = body.getLinearVelocity();
  const toward = Math.max(0, v.x * dir.x + v.y * dir.y);
  return timeToCover(distance, toward, acceleration, maxSpeed);
}

/** Distance needed to stop the motion along `direction` at `deceleration`:
 * `Kinematics.stoppingDistance(speedAlong(direction), deceleration)`. Mirrors C#
 * `GameplayVerbs2D.BrakingDistanceAlong`. */
export function brakingDistanceAlong(body: LinearBody, direction: Vec2Like, deceleration: number): number {
  return stoppingDistance(motion.speedAlong(body, direction), deceleration);
}

/** The body's up (local +Y) in the world (e.g. a one-way platform's top face):
 * `getWorldVector({ x: 0, y: 1 })`, copied. Mirrors C# `GameplayVerbs2D.UpDirection`. */
export function upDirection(body: Pick<Body2DLike, 'getWorldVector'>): Vec2Like {
  const v = body.getWorldVector({ x: 0, y: 1 });
  return { x: v.x, y: v.y };
}

/** Height of the world point above the body's center along its up (negative = below):
 * `getLocalPoint(worldPoint).y`. Mirrors C# `GameplayVerbs2D.HeightAbove`. */
export function heightAbove(body: Pick<Body2DLike, 'getLocalPoint'>, worldPoint: Vec2Like): number {
  return body.getLocalPoint(worldPoint).y;
}

/** Is the world point inside the body's box (half extents plus padding)? {@link BodyMotion2D.boxContains}.
 * Mirrors C# `GameplayVerbs2D.IsWithinBox`. */
export function isWithinBox(
  body: Pick<Body2DLike, 'getLocalPoint'>,
  worldPoint: Vec2Like,
  halfWidth: number,
  halfHeight: number,
  padX = 0,
  padY = 0,
): boolean {
  return motion.boxContains(body, worldPoint, halfWidth, halfHeight, padX, padY);
}

/** Is the world point at least `gap` away from the body's box?
 * `boxDistanceSquared(p, hw, hh) >= gap * gap`. Mirrors C# `GameplayVerbs2D.IsClearOfBox`. */
export function isClearOfBox(body: Pick<Body2DLike, 'getLocalPoint'>, worldPoint: Vec2Like, halfWidth: number, halfHeight: number, gap: number): boolean {
  return motion.boxDistanceSquared(body, worldPoint, halfWidth, halfHeight) >= gap * gap;
}
