/**
 * Physics layer. Velocity operations — mirror of C# `Altruist.Physx.TwoD.Velocity2D`.
 * Side view, +Y up, `gravity` = magnitude of the pull toward -Y.
 */
import type { Vec2Like } from '../math/vec2.ts';

/** How fast the velocity closes in against the normal: `-(v·n)`. */
export function approachSpeed(velocity: Vec2Like, normal: Vec2Like): number {
  return -(velocity.x * normal.x + velocity.y * normal.y);
}

/** How fast `a` closes in on `b` along the unit `along`: `(a.x - b.x) * along.x + (a.y - b.y) * along.y`
 * (the difference first). */
export function closingSpeed(a: Vec2Like, b: Vec2Like, along: Vec2Like): number {
  return (a.x - b.x) * along.x + (a.y - b.y) * along.y;
}

/** Centripetal acceleration on a curve of `radius`: `(v.x * v.x + v.y * v.y) / radius`. */
export function centripetalAcceleration(velocity: Vec2Like, radius: number): number {
  return (velocity.x * velocity.x + velocity.y * velocity.y) / radius;
}

/** Removes `amount` (1 = all) of the motion into `normal`:
 * `d = v·n; d < 0 ? (v.x - n.x * d * amount, v.y - n.y * d * amount) : v`. */
export function cancelInto(velocity: Vec2Like, normal: Vec2Like, amount = 1): Vec2Like {
  const d = velocity.x * normal.x + velocity.y * normal.y;
  if (d < 0) return { x: velocity.x - normal.x * d * amount, y: velocity.y - normal.y * d * amount };
  return { x: velocity.x, y: velocity.y };
}

/** `along = v·n; tangent = (v - n * along) * tangentKeep; n * normalSpeed + tangent`. */
export function bounce(velocity: Vec2Like, normal: Vec2Like, normalSpeed: number, tangentKeep: number): Vec2Like {
  const along = velocity.x * normal.x + velocity.y * normal.y;
  const tx = (velocity.x - normal.x * along) * tangentKeep;
  const ty = (velocity.y - normal.y * along) * tangentKeep;
  return { x: normal.x * normalSpeed + tx, y: normal.y * normalSpeed + ty };
}

/** Velocity of a rigid-body point: `linear + (-angular * offset.y, angular * offset.x)`. */
export function pointVelocity(linear: Vec2Like, angular: number, offset: Vec2Like): Vec2Like {
  return { x: linear.x + -angular * offset.y, y: linear.y + angular * offset.x };
}

/** `(v.x + d.x * acceleration * dt, v.y + d.y * acceleration * dt)`. */
export function accelerateAlong(velocity: Vec2Like, direction: Vec2Like, acceleration: number, dt: number): Vec2Like {
  return { x: velocity.x + direction.x * acceleration * dt, y: velocity.y + direction.y * acceleration * dt };
}

/** `f = 1 - drag * dt; (v.x * f, v.y * f)`. */
export function applyLinearDrag(velocity: Vec2Like, drag: number, dt: number): Vec2Like {
  const f = 1 - drag * dt;
  return { x: velocity.x * f, y: velocity.y * f };
}

/** `(v.x, v.y - gravity * dt * scale)`. */
export function applyGravity(velocity: Vec2Like, gravity: number, dt: number, scale = 1): Vec2Like {
  return { x: velocity.x, y: velocity.y - gravity * dt * scale };
}

/** When `|v.x| < minSpeed`: x = `dir * minSpeed`, `dir = v.x !== 0 ? Math.sign(v.x) : fallbackDirection`. */
export function keepMinimumSpeedX(velocity: Vec2Like, minSpeed: number, fallbackDirection: number): Vec2Like {
  if (!(Math.abs(velocity.x) < minSpeed)) return { x: velocity.x, y: velocity.y };
  const dir = velocity.x !== 0 ? Math.sign(velocity.x) : fallbackDirection;
  return { x: dir * minSpeed, y: velocity.y };
}
