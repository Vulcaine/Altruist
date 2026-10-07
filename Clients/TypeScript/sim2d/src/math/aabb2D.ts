/**
 * Math layer. Axis-aligned rectangles as `{ minX, maxX, minY, maxY }` — mirror of C#
 * `Altruist.TwoD.Numerics.Aabb2D` (bounds inclusive).
 */
import type { Vec2Like } from './vec2.ts';

export interface Aabb2DLike {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

export function fromCorners(a: Vec2Like, b: Vec2Like): Aabb2DLike {
  return { minX: Math.min(a.x, b.x), maxX: Math.max(a.x, b.x), minY: Math.min(a.y, b.y), maxY: Math.max(a.y, b.y) };
}

/** `((minX + maxX) / 2, (minY + maxY) / 2)`. */
export function center(box: Aabb2DLike): Vec2Like {
  return { x: (box.minX + box.maxX) / 2, y: (box.minY + box.maxY) / 2 };
}

/** `x >= minX - marginX && x <= maxX + marginX && y >= minY - marginY && y <= maxY + marginY`
 * (margins default to 0, which gives the plain bounds test). */
export function contains(box: Aabb2DLike, x: number, y: number, marginX = 0, marginY = 0): boolean {
  return x >= box.minX - marginX && x <= box.maxX + marginX && y >= box.minY - marginY && y <= box.maxY + marginY;
}

/** Overlap (touching edges count). */
export function intersects(a: Aabb2DLike, b: Aabb2DLike): boolean {
  return a.minX <= b.maxX && a.maxX >= b.minX && a.minY <= b.maxY && a.maxY >= b.minY;
}
