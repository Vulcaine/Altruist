/**
 * Math layer. Counter-clockwise rotations (physics-body convention, local +Y = up) — mirror of
 * C# `Altruist.TwoD.Numerics.Rotation2D`.
 */
import type { Vec2Like } from './vec2.ts';

/** Angle at which a body's local +Y points along `normal`: `Math.atan2(-normal.x, normal.y)`. */
export function angleAligningUp(normal: Vec2Like): number {
  return Math.atan2(-normal.x, normal.y);
}

/** Angle at which a body facing `facing` (> 0: front = local +x, else local -x) points its front
 * along `direction`: `Math.atan2(direction.y, direction.x) - (facing > 0 ? 0 : Math.PI)`. */
export function angleAligningForward(direction: Vec2Like, facing: number): number {
  return Math.atan2(direction.y, direction.x) - (facing > 0 ? 0 : Math.PI);
}

/** World direction of a body's local +Y at rotation `radians`: `(-sin, cos)`. */
export function upAt(radians: number): Vec2Like {
  return { x: -Math.sin(radians), y: Math.cos(radians) };
}

/** Local to world: `(c * x - s * y, s * x + c * y)`. */
export function rotate(radians: number, v: Vec2Like): Vec2Like {
  const c = Math.cos(radians);
  const s = Math.sin(radians);
  return { x: c * v.x - s * v.y, y: s * v.x + c * v.y };
}

/** World to local: `(c * x + s * y, -s * x + c * y)`. */
export function unrotate(radians: number, v: Vec2Like): Vec2Like {
  const c = Math.cos(radians);
  const s = Math.sin(radians);
  return { x: c * v.x + s * v.y, y: -s * v.x + c * v.y };
}
