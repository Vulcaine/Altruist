/**
 * Physics layer. Free flight under gravity — mirror of C# `Altruist.Physx.TwoD.Ballistics2D`.
 * +Y up, `gravity` ≥ 0 pulls toward -Y. The stepper is semi-implicit Euler, not the engine.
 */
import { contains, type Aabb2DLike } from '../math/aabb2D.ts';
import type { Vec2Like } from '../math/vec2.ts';

/** A mutable flight state for the stepper (C# passes position and velocity by ref). */
export interface FlightState {
  position: Vec2Like;
  velocity: Vec2Like;
}

/** `(p.x + v.x * t, p.y + v.y * t - 0.5 * gravity * t * t)`. */
export function positionAt(position: Vec2Like, velocity: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: position.x + velocity.x * t, y: position.y + velocity.y * t - 0.5 * gravity * t * t };
}

/** `(v.x, v.y - gravity * t)`. */
export function velocityAt(velocity: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: velocity.x, y: velocity.y - gravity * t };
}

/** `vy > 0 ? vy * vy / (2 * gravity) : 0`. */
export function apexHeight(verticalSpeed: number, gravity: number): number {
  return verticalSpeed > 0 ? (verticalSpeed * verticalSpeed) / (2 * gravity) : 0;
}

/** `(rise + 0.5 * gravity * t * t) / t`. */
export function launchSpeedY(rise: number, gravity: number, t: number): number {
  return (rise + 0.5 * gravity * t * t) / t;
}

/** `(delta.x / t, launchSpeedY(delta.y, gravity, t))`. */
export function launchVelocity(delta: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: delta.x / t, y: launchSpeedY(delta.y, gravity, t) };
}

/** In place: `v.y -= gravity * dt; p.x += v.x * dt; p.y += v.y * dt`. */
export function step(state: FlightState, gravity: number, dt: number): void {
  state.velocity = { x: state.velocity.x, y: state.velocity.y - gravity * dt };
  state.position = { x: state.position.x + state.velocity.x * dt, y: state.position.y + state.velocity.y * dt };
}

/** In place: `if (p.y < floorY && v.y < 0) { p.y = floorY; v.y = -v.y * restitution; }`; returns whether it bounced. */
export function bounceOnFloor(state: FlightState, floorY: number, restitution: number): boolean {
  if (!(state.position.y < floorY && state.velocity.y < 0)) return false;
  state.position = { x: state.position.x, y: floorY };
  state.velocity = { x: state.velocity.x, y: -state.velocity.y * restitution };
  return true;
}

/** State after `steps` steps (each followed by a floor bounce when `floorY` is given). */
export function predict(
  position: Vec2Like,
  velocity: Vec2Like,
  gravity: number,
  dt: number,
  steps: number,
  floorY?: number,
  restitution = 0,
): FlightState {
  const s: FlightState = { position: { x: position.x, y: position.y }, velocity: { x: velocity.x, y: velocity.y } };
  for (let i = 0; i < steps; i++) {
    step(s, gravity, dt);
    if (floorY !== undefined) bounceOnFloor(s, floorY, restitution);
  }
  return s;
}

/** 1-based number of the first step whose position satisfies `predicate`, or -1. */
export function firstStepWhere(
  position: Vec2Like,
  velocity: Vec2Like,
  gravity: number,
  dt: number,
  steps: number,
  predicate: (p: Vec2Like) => boolean,
): number {
  const s: FlightState = { position: { x: position.x, y: position.y }, velocity: { x: velocity.x, y: velocity.y } };
  for (let i = 0; i < steps; i++) {
    step(s, gravity, dt);
    if (predicate(s.position)) return i + 1;
  }
  return -1;
}

/** The first of `zones` (checked in order) a step's position is in, as its index and the 1-based
 * step; `{ zone: -1, step: -1 }` when none, or when the position drops below `floorY` first (checked
 * before the zones). Each step is `step()` (`v.y -= g * dt; p.x += v.x * dt; p.y += v.y * dt`).
 * Margins: pass `Aabb2D.grow(zone, mx, my)`. */
export function firstZoneEntered(
  position: Vec2Like,
  velocity: Vec2Like,
  gravity: number,
  dt: number,
  steps: number,
  zones: readonly Aabb2DLike[],
  floorY = -Infinity,
): { zone: number; step: number } {
  const s: FlightState = { position: { x: position.x, y: position.y }, velocity: { x: velocity.x, y: velocity.y } };
  for (let i = 0; i < steps; i++) {
    step(s, gravity, dt);
    if (s.position.y < floorY) return { zone: -1, step: -1 };
    for (let k = 0; k < zones.length; k++) if (contains(zones[k]!, s.position.x, s.position.y)) return { zone: k, step: i + 1 };
  }
  return { zone: -1, step: -1 };
}
