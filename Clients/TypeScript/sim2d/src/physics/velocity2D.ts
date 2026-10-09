/**
 * Physics layer. Velocity operations — mirror of C# `Altruist.Physx.TwoD.Velocity2D`.
 * Side view, +Y up, `gravity` = magnitude of the pull toward -Y.
 */
import type { Vec2Like } from '../math/vec2.ts';

/** How fast the velocity closes in against the normal (positive = moving into the surface):
 * `-(v·n)`. Pure value function (no body). Mirrors C# `Velocity2D.ApproachSpeed`; for a body use
 * {@link GameplayVerbs2D.impactSpeedAgainst}. Same as {@link NormalFrame2D.into}. */
export function approachSpeed(velocity: Vec2Like, normal: Vec2Like): number {
  return -(velocity.x * normal.x + velocity.y * normal.y);
}

/** How fast `a` closes in on `b` along the unit `along`: `(a.x - b.x) * along.x + (a.y - b.y) * along.y`
 * (the difference first). Mirrors C# `Velocity2D.ClosingSpeed`. */
export function closingSpeed(a: Vec2Like, b: Vec2Like, along: Vec2Like): number {
  return (a.x - b.x) * along.x + (a.y - b.y) * along.y;
}

/** Centripetal acceleration on a curve of `radius`: `(v.x * v.x + v.y * v.y) / radius` (no square
 * root). Mirrors C# `Velocity2D.CentripetalAcceleration`; scalar form {@link Kinematics.centripetalAcceleration}. */
export function centripetalAcceleration(velocity: Vec2Like, radius: number): number {
  return (velocity.x * velocity.x + velocity.y * velocity.y) / radius;
}

/** Removes `amount` (1 = all) of the motion into `normal`:
 * `d = v·n; d < 0 ? (v.x - n.x * d * amount, v.y - n.y * d * amount) : v` (always a copy). Motion
 * away from the surface is untouched. Pure; the body form is {@link BodyMotion2D.cancelVelocityInto}
 * and the gameplay verb {@link GameplayVerbs2D.cancelMotionAgainst}. Mirrors C# `Velocity2D.CancelInto`. */
export function cancelInto(velocity: Vec2Like, normal: Vec2Like, amount = 1): Vec2Like {
  const d = velocity.x * normal.x + velocity.y * normal.y;
  if (d < 0) return { x: velocity.x - normal.x * d * amount, y: velocity.y - normal.y * d * amount };
  return { x: velocity.x, y: velocity.y };
}

/**
 * Bounce of a velocity off a surface: the outgoing normal speed is set to `normalSpeed` (not
 * reflected or scaled) and `tangentKeep` of the tangential part is kept:
 * `along = v·n; tangent = (v - n * along) * tangentKeep; n * normalSpeed + tangent`.
 *
 * Math on a value only. Which one to pick:
 * - `bounce` (this): you hold a velocity value (e.g. one captured in pre-solve) and want the result.
 * - {@link BodyMotion2D.bounceVelocity}: same formula applied to a body's current velocity, written back.
 * - {@link GameplayVerbs2D.bounceOff}: intent-level verb; optionally takes a recorded `incoming`.
 *
 * Mirrors C# `Velocity2D.Bounce`.
 * @param velocity - Incoming velocity.
 * @param normal - Unit surface normal (pointing away from the surface).
 * @param normalSpeed - Outgoing speed along the normal (e.g. `restitution * approachSpeed`).
 * @param tangentKeep - Fraction of the tangential velocity kept (1 = frictionless).
 */
export function bounce(velocity: Vec2Like, normal: Vec2Like, normalSpeed: number, tangentKeep: number): Vec2Like {
  const along = velocity.x * normal.x + velocity.y * normal.y;
  const tx = (velocity.x - normal.x * along) * tangentKeep;
  const ty = (velocity.y - normal.y * along) * tangentKeep;
  return { x: normal.x * normalSpeed + tx, y: normal.y * normalSpeed + ty };
}

/** Velocity of a rigid-body point at `offset` from the center of mass:
 * `linear + (-angular * offset.y, angular * offset.x)` (angular in rad/s, CCW). Mirrors C#
 * `Velocity2D.PointVelocity`; on a body use {@link BodyMotion2D.velocityAtPoint}. */
export function pointVelocity(linear: Vec2Like, angular: number, offset: Vec2Like): Vec2Like {
  return { x: linear.x + -angular * offset.y, y: linear.y + angular * offset.x };
}

/** Constant acceleration along `direction` for one step of `dt` seconds (no target, no cap):
 * `(v.x + d.x * acceleration * dt, v.y + d.y * acceleration * dt)`. Mirrors C#
 * `Velocity2D.AccelerateAlong`; body form {@link BodyMotion2D.accelerateAlong}. */
export function accelerateAlong(velocity: Vec2Like, direction: Vec2Like, acceleration: number, dt: number): Vec2Like {
  return { x: velocity.x + direction.x * acceleration * dt, y: velocity.y + direction.y * acceleration * dt };
}

/** Linear drag for one step: `f = 1 - drag * dt; (v.x * f, v.y * f)` (`drag` in 1/s; explicit Euler,
 * so `drag * dt > 1` reverses the velocity, not clamped). Mirrors C# `Velocity2D.ApplyLinearDrag`;
 * body form {@link BodyMotion2D.applyLinearDrag}. */
export function applyLinearDrag(velocity: Vec2Like, drag: number, dt: number): Vec2Like {
  const f = 1 - drag * dt;
  return { x: velocity.x * f, y: velocity.y * f };
}

/** Hand-applied gravity for one step: `(v.x, v.y - gravity * dt * scale)` (`gravity` ≥ 0 pulls
 * toward -Y). For worlds whose engine gravity is zero; with engine gravity this would double it.
 * Mirrors C# `Velocity2D.ApplyGravity`; body form {@link BodyMotion2D.applyGravity}, verb
 * {@link GameplayVerbs2D.fall}. */
export function applyGravity(velocity: Vec2Like, gravity: number, dt: number, scale = 1): Vec2Like {
  return { x: velocity.x, y: velocity.y - gravity * dt * scale };
}

/** Keeps the horizontal speed at least `minSpeed`: when `|v.x| < minSpeed`, x = `dir * minSpeed`,
 * `dir = v.x !== 0 ? Math.sign(v.x) : fallbackDirection`; otherwise (and for NaN) a copy of `v`.
 * Mirrors C# `Velocity2D.KeepMinimumSpeedX`. */
export function keepMinimumSpeedX(velocity: Vec2Like, minSpeed: number, fallbackDirection: number): Vec2Like {
  if (!(Math.abs(velocity.x) < minSpeed)) return { x: velocity.x, y: velocity.y };
  const dir = velocity.x !== 0 ? Math.sign(velocity.x) : fallbackDirection;
  return { x: dir * minSpeed, y: velocity.y };
}
