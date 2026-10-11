/**
 * Physics layer. Free flight under gravity — mirror of C# `Altruist.Physx.TwoD.Ballistics2D`.
 * +Y up, `gravity` ≥ 0 pulls toward -Y. The stepper is semi-implicit Euler, not the engine.
 */
import { contains, type Aabb2DLike } from '../math/aabb2D.ts';
import type { Vec2Like } from '../math/vec2.ts';

/** A mutable flight state for the stepper (C# passes position and velocity by ref). */
export interface FlightState {
  /** Position in world units; replaced (not mutated) by each {@link step}. */
  position: Vec2Like;
  /** Velocity in units per second; replaced (not mutated) by each {@link step}. */
  velocity: Vec2Like;
}

/** Closed-form position after `t` seconds of free flight: `(p.x + v.x * t, p.y + v.y * t - 0.5 * gravity * t * t)`.
 * Exact parabola; differs from the stepped {@link predict} by the Euler error. Mirrors C#
 * `Ballistics2D.PositionAt`; for a body use {@link GameplayVerbs2D.predictPosition}. */
export function positionAt(position: Vec2Like, velocity: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: position.x + velocity.x * t, y: position.y + velocity.y * t - 0.5 * gravity * t * t };
}

/** Closed-form velocity after `t` seconds: `(v.x, v.y - gravity * t)`. Mirrors C# `Ballistics2D.VelocityAt`. */
export function velocityAt(velocity: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: velocity.x, y: velocity.y - gravity * t };
}

/** Height still gained before the apex when rising at `verticalSpeed`: `vy > 0 ? vy * vy / (2 * gravity) : 0`.
 * Mirrors C# `Ballistics2D.ApexHeight`; for a body use {@link GameplayVerbs2D.riseLeft}. */
export function apexHeight(verticalSpeed: number, gravity: number): number {
  return verticalSpeed > 0 ? (verticalSpeed * verticalSpeed) / (2 * gravity) : 0;
}

/** Vertical launch speed that rises `rise` (negative = drop) in exactly `t` seconds:
 * `(rise + 0.5 * gravity * t * t) / t`. Mirrors C# `Ballistics2D.LaunchSpeedY`. */
export function launchSpeedY(rise: number, gravity: number, t: number): number {
  return (rise + 0.5 * gravity * t * t) / t;
}

/** Launch velocity that covers `delta` in exactly `t` seconds of free flight:
 * `(delta.x / t, launchSpeedY(delta.y, gravity, t))`. Mirrors C# `Ballistics2D.LaunchVelocity`;
 * from a body use {@link GameplayVerbs2D.velocityToHit}. */
export function launchVelocity(delta: Vec2Like, gravity: number, t: number): Vec2Like {
  return { x: delta.x / t, y: launchSpeedY(delta.y, gravity, t) };
}

/** One semi-implicit Euler step, in place (replaces `state.position` / `state.velocity` with new
 * objects): `v.y -= gravity * dt; p.x += v.x * dt; p.y += v.y * dt`. Mirrors C# `Ballistics2D.Step`
 * (`ref` parameters there). */
export function step(state: FlightState, gravity: number, dt: number): void {
  state.velocity = { x: state.velocity.x, y: state.velocity.y - gravity * dt };
  state.position = { x: state.position.x + state.velocity.x * dt, y: state.position.y + state.velocity.y * dt };
}

/** Floor at height `floorY` (e.g. ground + radius), in place:
 * `if (p.y < floorY && v.y < 0) { p.y = floorY; v.y = -v.y * restitution; }`; returns whether it
 * bounced. Mirrors C# `Ballistics2D.BounceOnFloor`. */
export function bounceOnFloor(state: FlightState, floorY: number, restitution: number): boolean {
  if (!(state.position.y < floorY && state.velocity.y < 0)) return false;
  state.position = { x: state.position.x, y: floorY };
  state.velocity = { x: state.velocity.x, y: -state.velocity.y * restitution };
  return true;
}

/** State after `steps` calls of {@link step}, each followed by {@link bounceOnFloor} when `floorY` is
 * given. Copies the inputs; returns a new state. Mirrors both C# `Ballistics2D.Predict` overloads
 * (with and without `floorY, restitution`). */
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

/** Steps up to `steps` times and returns the 1-based number of the first step whose position
 * satisfies `predicate`, or -1. The position object passed to `predicate` is fresh each step.
 * Mirrors C# `Ballistics2D.FirstStepWhere`. */
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
 * Margins: pass `Aabb2D.grow(zone, mx, my)`. Mirrors C# `Ballistics2D.FirstZoneEntered`; for a body
 * use {@link GameplayVerbs2D.predictZoneEntry}. */
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
