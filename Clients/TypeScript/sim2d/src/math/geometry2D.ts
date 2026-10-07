/**
 * Math layer. Point-versus-box and point-versus-ray — mirror of C# `Altruist.TwoD.Numerics.Geometry2D`.
 * Box tests take the point in the box's local frame (e.g. `body.getLocalPoint(p)`).
 */
import type { Vec2Like } from './vec2.ts';

/** `!(Math.abs(local.x) > halfWidth || Math.abs(local.y) > halfHeight)`. */
export function boxContainsLocal(local: Vec2Like, halfWidth: number, halfHeight: number): boolean {
  return !(Math.abs(local.x) > halfWidth || Math.abs(local.y) > halfHeight);
}

/** `dx = max(0, |x| - hw); dy = max(0, |y| - hh); dx * dx + dy * dy`. */
export function boxDistanceSquaredLocal(local: Vec2Like, halfWidth: number, halfHeight: number): number {
  const dx = Math.max(0, Math.abs(local.x) - halfWidth);
  const dy = Math.max(0, Math.abs(local.y) - halfHeight);
  return dx * dx + dy * dy;
}

/** `halfWidth * |sin(angle)| + halfHeight * |cos(angle)|`. */
export function rotatedBoxHalfExtentY(halfWidth: number, halfHeight: number, angle: number): number {
  return halfWidth * Math.abs(Math.sin(angle)) + halfHeight * Math.abs(Math.cos(angle));
}

/** `halfWidth * |cos(angle)| + halfHeight * |sin(angle)|`. */
export function rotatedBoxHalfExtentX(halfWidth: number, halfHeight: number, angle: number): number {
  return halfWidth * Math.abs(Math.cos(angle)) + halfHeight * Math.abs(Math.sin(angle));
}

/** Distance along the ray and perpendicular offset:
 * `along = (p.x - o.x) * d.x + (p.y - o.y) * d.y`, `offset = |(p.x - o.x) * d.y - (p.y - o.y) * d.x|`. */
export function pointToRay(origin: Vec2Like, direction: Vec2Like, point: Vec2Like): { along: number; offset: number } {
  const along = (point.x - origin.x) * direction.x + (point.y - origin.y) * direction.y;
  const offset = Math.abs((point.x - origin.x) * direction.y - (point.y - origin.y) * direction.x);
  return { along, offset };
}
