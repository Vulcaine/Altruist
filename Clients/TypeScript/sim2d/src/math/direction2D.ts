/**
 * Math layer. Directions — mirror of C# `Altruist.TwoD.Numerics.Direction2D`. Lengths are
 * `Math.sqrt(x * x + y * y)` like C# (not `Math.hypot`, which can differ in the last bit).
 */
import { toRadians } from './angle.ts';
import type { Vec2Like } from './vec2.ts';

/** Unit vector from `from` to `to`; `(0, 0)` when they are closer than 1e-6 (squared length below
 * 1e-12). Mirrors C# `Direction2D.Between` (also aliased `Direction2D.Toward` in C#). Unlike
 * {@link VectorMath2D.normalizeOrZero} (no epsilon) this treats tiny gaps as zero. */
export function between(from: Vec2Like, to: Vec2Like): Vec2Like {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const lenSq = dx * dx + dy * dy;
  if (lenSq < 1e-12) return { x: 0, y: 0 };
  const l = Math.sqrt(lenSq);
  return { x: dx / l, y: dy / l };
}

/** Unit vector in the yaw convention (0 = +Y, clockwise positive): `(sin, cos)`. Mirrors C#
 * `Direction2D.TowardAngle`. Not the math/body convention of {@link fromPolar} (0 = +X,
 * counter-clockwise) nor {@link Rotation2D.upAt}. */
export function towardAngle(rotationRadians: number): Vec2Like {
  return { x: Math.sin(rotationRadians), y: Math.cos(rotationRadians) };
}

/** Rotates 90° counter-clockwise: `(-y, x)`. Mirrors C# `Direction2D.Perpendicular`. */
export function perpendicular(dir: Vec2Like): Vec2Like {
  return { x: -dir.y, y: dir.x };
}

/** Rotates 90° clockwise: `(y, -x)` (the +X tangent of a floor normal `(0, 1)`). Mirrors C#
 * `Direction2D.PerpendicularClockwise`. */
export function perpendicularClockwise(dir: Vec2Like): Vec2Like {
  return { x: dir.y, y: -dir.x };
}

/** Unit vector at a polar angle in the math / physics-body convention (0 = +X, counter-clockwise,
 * radians): `(cos, sin)`. Mirrors C# `Direction2D.FromPolar`. For the yaw convention use
 * {@link towardAngle}. */
export function fromPolar(radians: number): Vec2Like {
  return { x: Math.cos(radians), y: Math.sin(radians) };
}

/** {@link fromPolar} in degrees: `fromPolar(degrees * (π / 180))`. Mirrors C# `Direction2D.FromPolarDegrees`. */
export function fromPolarDegrees(degrees: number): Vec2Like {
  return fromPolar(toRadians(degrees));
}

/** Limits a unit vector's Y (elevation) to [minY, maxY], rebuilding X on its side so the result
 * stays unit length: `x = sign(x) * sqrt(1 - y * y)`. `maxY` is checked first; a vertical input
 * (x = 0) keeps x = 0. Mirrors C# `Direction2D.ClampElevation`. */
export function clampElevation(unit: Vec2Like, minY: number, maxY: number): Vec2Like {
  let ux = unit.x;
  let uy = unit.y;
  if (uy > maxY) {
    uy = maxY;
    ux = Math.sign(ux) * Math.sqrt(1 - uy * uy);
  } else if (uy < minY) {
    uy = minY;
    ux = Math.sign(ux) * Math.sqrt(1 - uy * uy);
  }
  return { x: ux, y: uy };
}
