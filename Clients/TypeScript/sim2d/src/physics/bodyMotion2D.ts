/**
 * Physics layer. Velocity and angular-velocity operations on a body — mirror of C#
 * `Altruist.Physx.TwoD.BodyMotionExtensions2D` (extension methods there, `fn(body, ...)` here).
 * Each reads the body, applies one math-layer / Velocity2D formula and writes once.
 */
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

/** `v.x * d.x + v.y * d.y`. */
export function speedAlong(body: LinearBody, direction: Vec2Like): number {
  const v = body.getLinearVelocity();
  return v.x * direction.x + v.y * direction.y;
}

/** `pointVelocity(v, w, worldPoint - worldCenter)`. */
export function velocityAtPoint(body: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>, worldPoint: Vec2Like): Vec2Like {
  const c = body.getWorldCenter();
  return pointVelocity(body.getLinearVelocity(), body.getAngularVelocity(), { x: worldPoint.x - c.x, y: worldPoint.y - c.y });
}

/** `boxContainsLocal(getLocalPoint(p), halfWidth + padX, halfHeight + padY)`. */
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

/** `boxDistanceSquaredLocal(getLocalPoint(p), halfWidth, halfHeight)`. */
export function boxDistanceSquared(body: Pick<Body2DLike, 'getLocalPoint'>, worldPoint: Vec2Like, halfWidth: number, halfHeight: number): number {
  return boxDistanceSquaredLocal(body.getLocalPoint(worldPoint), halfWidth, halfHeight);
}

// ── Linear velocity ───────────────────────────────────────────────────

export function addVelocityAlong(body: LinearBody, direction: Vec2Like, amount: number): void {
  body.setLinearVelocity(addAlong(body.getLinearVelocity(), direction, amount));
}

export function setVelocityAlong(body: LinearBody, direction: Vec2Like, speed: number): void {
  body.setLinearVelocity(withComponentAlong(body.getLinearVelocity(), direction, speed));
}

/** `approachComponentAlong(v, direction, target, acceleration * dt)`. */
export function approachVelocityAlong(body: LinearBody, direction: Vec2Like, target: number, acceleration: number, dt: number): void {
  body.setLinearVelocity(approachComponentAlong(body.getLinearVelocity(), direction, target, acceleration * dt));
}

export function redirectVelocityAlong(body: LinearBody, axis: Vec2Like): void {
  body.setLinearVelocity(redirectAlong(body.getLinearVelocity(), axis));
}

export function accelerateAlong(body: LinearBody, direction: Vec2Like, acceleration: number, dt: number): void {
  body.setLinearVelocity(accelerate(body.getLinearVelocity(), direction, acceleration, dt));
}

export function cancelVelocityInto(body: LinearBody, normal: Vec2Like, amount = 1): void {
  body.setLinearVelocity(cancelInto(body.getLinearVelocity(), normal, amount));
}

export function bounceVelocity(body: LinearBody, normal: Vec2Like, normalSpeed: number, tangentKeep: number): void {
  body.setLinearVelocity(bounce(body.getLinearVelocity(), normal, normalSpeed, tangentKeep));
}

/** `speed = |v|; shortenBy(v, speed, amount)` (no-op at rest). */
export function reduceSpeed(body: LinearBody, amount: number): void {
  const v = body.getLinearVelocity();
  const speed = Math.sqrt(v.x * v.x + v.y * v.y);
  if (speed === 0) return;
  body.setLinearVelocity(shortenBy(v, speed, amount));
}

/** Written only when faster than `maxSpeed`. */
export function clampSpeed(body: LinearBody, maxSpeed: number): void {
  const v = body.getLinearVelocity();
  const s = Math.sqrt(v.x * v.x + v.y * v.y);
  if (s > maxSpeed) body.setLinearVelocity(clampLength(v, maxSpeed));
}

export function applyLinearDrag(body: LinearBody, drag: number, dt: number): void {
  body.setLinearVelocity(dragStep(body.getLinearVelocity(), drag, dt));
}

export function applyGravity(body: LinearBody, gravity: number, dt: number, scale = 1): void {
  body.setLinearVelocity(gravityStep(body.getLinearVelocity(), gravity, dt, scale));
}

/** Written only when it changes the velocity. */
export function keepMinimumSpeedX(body: LinearBody, minSpeed: number, fallbackDirection: number): void {
  const v = body.getLinearVelocity();
  if (!(Math.abs(v.x) < minSpeed)) return;
  body.setLinearVelocity(keepMinX(v, minSpeed, fallbackDirection));
}

/** `v = before + (v - before) * keep; w = wBefore + (w - wBefore) * keep`. */
export function blendMotionFrom(body: LinearBody & AngularBody, linearBefore: Vec2Like, angularBefore: number, keep: number): void {
  body.setLinearVelocity(lerp(linearBefore, body.getLinearVelocity(), keep));
  body.setAngularVelocity(lerpScalar(angularBefore, body.getAngularVelocity(), keep));
}

// ── Angular velocity ──────────────────────────────────────────────────

/** `w = wrap(angleAligningUp(normal) - angle) * rate`. */
export function alignUpToNormal(body: AngularBody, normal: Vec2Like, rate: number): void {
  body.setAngularVelocity(wrap(angleAligningUp(normal) - body.getAngle()) * rate);
}

export function arriveAtAngle(body: AngularBody, targetAngle: number, time: number, minTime: number): void {
  body.setAngularVelocity(arriveRate(body.getAngle(), targetAngle, time, minTime));
}

export function rotateTowardAngle(body: AngularBody, targetAngle: number, gain: number): void {
  body.setAngularVelocity(rateToward(body.getAngle(), targetAngle, gain));
}

/** `w = approach(w, targetRate, angularAcceleration * dt)`. */
export function approachAngularVelocity(body: AngularBody, targetRate: number, angularAcceleration: number, dt: number): void {
  body.setAngularVelocity(approach(body.getAngularVelocity(), targetRate, angularAcceleration * dt));
}

/** `target = clampedRateToward(angle, targetAngle, gain, maxRate); w = approach(w, target, angularAcceleration * dt)`. */
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

// ── State ─────────────────────────────────────────────────────────────

/** Copies transform and velocities, then wakes the body. */
export function copyMotionFrom(body: Body2DLike, source: Body2DLike): void {
  const p = source.getPosition();
  body.setTransform({ x: p.x, y: p.y }, source.getAngle());
  const v = source.getLinearVelocity();
  body.setLinearVelocity({ x: v.x, y: v.y });
  body.setAngularVelocity(source.getAngularVelocity());
  body.setAwake(true);
}
