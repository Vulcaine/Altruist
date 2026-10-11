/**
 * Math layer. Counter-clockwise rotations (physics-body convention, local +Y = up) — mirror of
 * C# `Altruist.TwoD.Numerics.Rotation2D`.
 */
import type { Vec2Like } from './vec2.ts';

/** Angle (radians, counter-clockwise) at which a body's local +Y points along `normal`:
 * `Math.atan2(-normal.x, normal.y)`. Turn a body toward it to align its up with a surface.
 * Mirrors C# `Rotation2D.AngleAligningUp`. */
export function angleAligningUp(normal: Vec2Like): number {
  return Math.atan2(-normal.x, normal.y);
}

/** Angle at which a body facing `facing` (> 0: front = local +x, else local -x) points its front
 * along `direction`: `Math.atan2(direction.y, direction.x) - (facing > 0 ? 0 : Math.PI)`. Not
 * wrapped (can fall outside [-π, π]). Mirrors C# `Rotation2D.AngleAligningForward`. */
export function angleAligningForward(direction: Vec2Like, facing: number): number {
  return Math.atan2(direction.y, direction.x) - (facing > 0 ? 0 : Math.PI);
}

/** World direction of a body's local +Y at rotation `radians`: `(-sin, cos)` (what
 * `getWorldVector((0, 1))` returns). Mirrors C# `Rotation2D.UpAt`. */
export function upAt(radians: number): Vec2Like {
  return { x: -Math.sin(radians), y: Math.cos(radians) };
}

/** Rotates `v` counter-clockwise by `radians` (local to world): `(c * x - s * y, s * x + c * y)`.
 * Mirrors the C# instance method `new Rotation2D(radians).Rotate(v)`. For degrees see
 * {@link VectorMath2D.rotateDegrees}. */
export function rotate(radians: number, v: Vec2Like): Vec2Like {
  const c = Math.cos(radians);
  const s = Math.sin(radians);
  return { x: c * v.x - s * v.y, y: s * v.x + c * v.y };
}

/** Inverse of {@link rotate} (world to local): `(c * x + s * y, -s * x + c * y)`. Mirrors the C#
 * instance method `new Rotation2D(radians).Unrotate(v)`. */
export function unrotate(radians: number, v: Vec2Like): Vec2Like {
  const c = Math.cos(radians);
  const s = Math.sin(radians);
  return { x: c * v.x + s * v.y, y: -s * v.x + c * v.y };
}
