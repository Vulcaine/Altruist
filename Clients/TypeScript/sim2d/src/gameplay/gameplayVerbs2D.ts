/**
 * Gameplay layer. Designer-level actions and questions on a body — mirror of C#
 * `Altruist.Gaming.TwoD.GameplayVerbs2D` (`body.JumpOff(n, s)` there, `jumpOff(body, n, s)` here).
 * Each verb delegates to one physics-layer operation (which delegates to the math layer) with the
 * same operation order, so it gives the same numbers as the hand-written update it replaces.
 * Side view, +Y up, `gravity` = magnitude of the pull toward -Y; counter-clockwise angles.
 */
import { nearestEquivalent, nearestFullTurn } from '../math/angle.ts';
import { contains, type Aabb2DLike } from '../math/aabb2D.ts';
import { angleAligningUp } from '../math/rotation2D.ts';
import type { Vec2Like } from '../math/vec2.ts';
import { addAlong, normalizeOrZeroWithLength } from '../math/vectorMath2D.ts';
import { apexHeight, firstStepWhere, launchVelocity, positionAt, predict, type FlightState } from '../physics/ballistics2D.ts';
import type { AngularBody, Body2DLike, LinearBody, PositionedBody } from '../physics/body2D.ts';
import * as motion from '../physics/bodyMotion2D.ts';
import { restore } from '../physics/bodyState2D.ts';
import { stoppingDistance, timeToCover } from '../physics/kinematics.ts';
import { approachSpeed, bounce, cancelInto } from '../physics/velocity2D.ts';

// ── Launching and jumping ─────────────────────────────────────────────

/** Jump off a surface: cancel motion into it, then add `speed` along its normal. */
export function jumpOff(body: LinearBody, surfaceNormal: Vec2Like, speed: number): void {
  const v = cancelInto(body.getLinearVelocity(), surfaceNormal);
  body.setLinearVelocity(addAlong(v, surfaceNormal, speed));
}

/** Add speed in a direction, keeping the current motion. */
export function pushAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  motion.addVelocityAlong(body, direction, speed);
}

/** Replace the motion with `speed` along the unit `direction`. */
export function launchAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  body.setLinearVelocity({ x: direction.x * speed, y: direction.y * speed });
}

/** `launchAlong` the body's own local axis turned into world space. */
export function launchForward(body: LinearBody & Pick<Body2DLike, 'getWorldVector'>, localForward: Vec2Like, speed: number): void {
  launchAlong(body, body.getWorldVector(localForward), speed);
}

/** Cancel `fraction` (1 = all) of the motion against `direction`. */
export function cancelMotionAgainst(body: LinearBody, direction: Vec2Like, fraction = 1): void {
  motion.cancelVelocityInto(body, direction, fraction);
}

/** Bounce / launch off a surface: `outSpeed` along the normal plus `tangentKeep` of the motion
 * along it, from `incoming` (a recorded velocity) or the current velocity. */
export function bounceOff(body: LinearBody, normal: Vec2Like, outSpeed: number, tangentKeep: number, incoming?: Vec2Like): void {
  body.setLinearVelocity(bounce(incoming ?? body.getLinearVelocity(), normal, outSpeed, tangentKeep));
}

/** Keep only `keep` of the velocity change a hit caused. */
export function absorbImpact(body: LinearBody & AngularBody, velocityBefore: Vec2Like, spinBefore: number, keep: number): void {
  motion.blendMotionFrom(body, velocityBefore, spinBefore, keep);
}

// ── Driving on surfaces ───────────────────────────────────────────────

/** Approach `targetSpeed` along the tangent at most `acceleration` per second. */
export function driveAlong(body: LinearBody, tangent: Vec2Like, targetSpeed: number, acceleration: number, dt: number): void {
  motion.approachVelocityAlong(body, tangent, targetSpeed, acceleration, dt);
}

/** Brake toward a stop along `direction`. */
export function brakeAlong(body: LinearBody, direction: Vec2Like, deceleration: number, dt: number): void {
  motion.approachVelocityAlong(body, direction, 0, deceleration, dt);
}

/** Set the speed along `direction`, keeping the rest. */
export function setSpeedAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  motion.setVelocityAlong(body, direction, speed);
}

/** Carry all speed along the surface tangent. */
export function followSurface(body: LinearBody, tangent: Vec2Like): void {
  motion.redirectVelocityAlong(body, tangent);
}

/** Press into a surface: `v - n * acceleration * dt`. */
export function stickTo(body: LinearBody, surfaceNormal: Vec2Like, acceleration: number, dt: number): void {
  motion.accelerateAlong(body, surfaceNormal, -acceleration, dt);
}

// ── Speed limits, drag, gravity ───────────────────────────────────────

export function clampTopSpeed(body: LinearBody, maxSpeed: number): void {
  motion.clampSpeed(body, maxSpeed);
}

export function applyDrag(body: LinearBody, drag: number, dt: number): void {
  motion.applyLinearDrag(body, drag, dt);
}

export function bleedSpeed(body: LinearBody, amount: number): void {
  motion.reduceSpeed(body, amount);
}

/** Fall one step: `v.y - gravity * dt * gravityScale`. */
export function fall(body: LinearBody, gravity: number, dt: number, gravityScale = 1): void {
  motion.applyGravity(body, gravity, dt, gravityScale);
}

/** Keep at least `minSpeed` horizontally (on `fallbackDirection` when still). */
export function keepRolling(body: LinearBody, minSpeed: number, fallbackDirection: number): void {
  motion.keepMinimumSpeedX(body, minSpeed, fallbackDirection);
}

// ── Orientation ───────────────────────────────────────────────────────

export function alignToSurface(body: AngularBody, normal: Vec2Like, rate: number): void {
  motion.alignUpToNormal(body, normal, rate);
}

export function turnToAngleIn(body: AngularBody, targetAngle: number, time: number, minTime: number): void {
  motion.arriveAtAngle(body, targetAngle, time, minTime);
}

export function aimAt(body: AngularBody, targetAngle: number, gain: number, maxRate: number, angularAcceleration: number, dt: number): void {
  motion.steerAngularVelocity(body, targetAngle, gain, maxRate, angularAcceleration, dt);
}

export function stopSpinning(body: AngularBody, angularAcceleration: number, dt: number): void {
  motion.approachAngularVelocity(body, 0, angularAcceleration, dt);
}

export function holdAngle(body: AngularBody, targetAngle: number, gain: number): void {
  motion.rotateTowardAngle(body, targetAngle, gain);
}

/** `nearestEquivalent(angle, angleAligningUp(normal))`. */
export function uprightAngleOn(body: Pick<AngularBody, 'getAngle'>, normal: Vec2Like): number {
  return nearestEquivalent(body.getAngle(), angleAligningUp(normal));
}

/** `nearestFullTurn(angle)`. */
export function levelAngle(body: Pick<AngularBody, 'getAngle'>): number {
  return nearestFullTurn(body.getAngle());
}

/** `setTransform(position - normal * sink, uprightAngleOn(normal)); w = 0`. */
export function snapUprightOn(body: Body2DLike, normal: Vec2Like, sink = 0): void {
  const angle = uprightAngleOn(body, normal);
  const p = body.getPosition();
  body.setTransform({ x: p.x - normal.x * sink, y: p.y - normal.y * sink }, angle);
  body.setAngularVelocity(0);
}

/** Place the body with a motion and wake it. */
export function resetMotion(body: Body2DLike, position: Vec2Like, angle: number, velocity: Vec2Like, spin: number): void {
  restore({ position, angle, linearVelocity: velocity, angularVelocity: spin, isAwake: true, isActive: true }, body);
}

// ── Questions ─────────────────────────────────────────────────────────

export function impactSpeedAgainst(body: LinearBody, normal: Vec2Like): number {
  return approachSpeed(body.getLinearVelocity(), normal);
}

export function velocityAt(body: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>, worldPoint: Vec2Like): Vec2Like {
  return motion.velocityAtPoint(body, worldPoint);
}

export function predictPosition(body: LinearBody & PositionedBody, t: number, gravity: number): Vec2Like {
  return positionAt(body.getPosition(), body.getLinearVelocity(), gravity, t);
}

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

export function riseLeft(body: LinearBody, gravity: number): number {
  return apexHeight(body.getLinearVelocity().y, gravity);
}

/** `!(height > riseLeft(gravity) + margin)`. */
export function canReachHeight(body: LinearBody, height: number, gravity: number, margin = 0): boolean {
  return !(height > riseLeft(body, gravity) + margin);
}

/** Will free flight (own velocity, or `velocity`) enter `zone` (with margins) within `lookahead` s
 * over `steps` steps of `lookahead / steps`? */
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

export function velocityToHit(body: PositionedBody, target: Vec2Like, gravity: number, time: number): Vec2Like {
  const p = body.getPosition();
  return launchVelocity({ x: target.x - p.x, y: target.y - p.y }, gravity, time);
}

export function timeToReach(body: LinearBody & PositionedBody, target: Vec2Like, acceleration: number, maxSpeed: number): number {
  const p = body.getPosition();
  const { direction: dir, length: distance } = normalizeOrZeroWithLength({ x: target.x - p.x, y: target.y - p.y });
  const v = body.getLinearVelocity();
  const toward = Math.max(0, v.x * dir.x + v.y * dir.y);
  return timeToCover(distance, toward, acceleration, maxSpeed);
}

export function brakingDistanceAlong(body: LinearBody, direction: Vec2Like, deceleration: number): number {
  return stoppingDistance(motion.speedAlong(body, direction), deceleration);
}

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

/** `boxDistanceSquared(p, hw, hh) >= gap * gap`. */
export function isClearOfBox(body: Pick<Body2DLike, 'getLocalPoint'>, worldPoint: Vec2Like, halfWidth: number, halfHeight: number, gap: number): boolean {
  return motion.boxDistanceSquared(body, worldPoint, halfWidth, halfHeight) >= gap * gap;
}
