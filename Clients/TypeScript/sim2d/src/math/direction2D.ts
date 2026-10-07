/**
 * Math layer. Directions — mirror of C# `Altruist.TwoD.Numerics.Direction2D`.
 */
import { toRadians } from './angle.ts';
import type { Vec2Like } from './vec2.ts';

/** Unit vector from `from` to `to`; zero when closer than 1e-6. */
export function between(from: Vec2Like, to: Vec2Like): Vec2Like {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const lenSq = dx * dx + dy * dy;
  if (lenSq < 1e-12) return { x: 0, y: 0 };
  const l = Math.sqrt(lenSq);
  return { x: dx / l, y: dy / l };
}

/** Yaw convention (0 = +Y, clockwise): `(sin, cos)`. */
export function towardAngle(rotationRadians: number): Vec2Like {
  return { x: Math.sin(rotationRadians), y: Math.cos(rotationRadians) };
}

/** 90° counter-clockwise: `(-y, x)`. */
export function perpendicular(dir: Vec2Like): Vec2Like {
  return { x: -dir.y, y: dir.x };
}

/** 90° clockwise: `(y, -x)` (the +X tangent of a floor normal). */
export function perpendicularClockwise(dir: Vec2Like): Vec2Like {
  return { x: dir.y, y: -dir.x };
}

/** Polar, math convention (0 = +X, counter-clockwise): `(cos, sin)`. */
export function fromPolar(radians: number): Vec2Like {
  return { x: Math.cos(radians), y: Math.sin(radians) };
}

/** `fromPolar(degrees * (π / 180))`. */
export function fromPolarDegrees(degrees: number): Vec2Like {
  return fromPolar(toRadians(degrees));
}

/** Limits a unit vector's Y to [minY, maxY], rebuilding X on its side: `x = sign(x) * sqrt(1 - y * y)`. */
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
