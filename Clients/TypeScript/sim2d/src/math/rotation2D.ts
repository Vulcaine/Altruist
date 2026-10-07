/**
 * Math layer. Counter-clockwise rotations (physics-body convention, local +Y = up) — mirror of
 * C# `Altruist.TwoD.Numerics.Rotation2D`.
 */
import type { Vec2Like } from './vec2.ts';

/** Angle at which a body's local +Y points along `normal`: `Math.atan2(-normal.x, normal.y)`. */
export function angleAligningUp(normal: Vec2Like): number {
  return Math.atan2(-normal.x, normal.y);
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
