/**
 * Math layer. Axis-aligned rectangles as `{ minX, maxX, minY, maxY }` — mirror of C#
 * `Altruist.TwoD.Numerics.Aabb2D` (bounds inclusive).
 */
import type { Vec2Like } from './vec2.ts';

/** Axis-aligned box with inclusive bounds; the plain-object form of C# `Aabb2D`
 * (record struct `Aabb2D(MinX, MaxX, MinY, MaxY)`). Expected `min <= max` on both axes. */
export interface Aabb2DLike {
  /** Left edge (smallest X). */
  minX: number;
  /** Right edge (largest X). */
  maxX: number;
  /** Bottom edge (smallest Y, +Y up). */
  minY: number;
  /** Top edge (largest Y). */
  maxY: number;
}

/** Box spanning two opposite corners in any order (per-axis `Math.min` / `Math.max`).
 * Mirrors C# `Aabb2D.FromCorners`. */
export function fromCorners(a: Vec2Like, b: Vec2Like): Aabb2DLike {
  return { minX: Math.min(a.x, b.x), maxX: Math.max(a.x, b.x), minY: Math.min(a.y, b.y), maxY: Math.max(a.y, b.y) };
}

/** Midpoint `((minX + maxX) / 2, (minY + maxY) / 2)`. Mirrors C# `Aabb2D.Center` (a property there). */
export function center(box: Aabb2DLike): Vec2Like {
  return { x: (box.minX + box.maxX) / 2, y: (box.minY + box.maxY) / 2 };
}

/** `x >= minX - marginX && x <= maxX + marginX && y >= minY - marginY && y <= maxY + marginY`
 * (margins default to 0, which gives the plain bounds test; edges are inside). Mirrors both C#
 * `Aabb2D.Contains(x, y)` and `Aabb2D.Contains(x, y, marginX, marginY)`. */
export function contains(box: Aabb2DLike, x: number, y: number, marginX = 0, marginY = 0): boolean {
  return x >= box.minX - marginX && x <= box.maxX + marginX && y >= box.minY - marginY && y <= box.maxY + marginY;
}

/** The box grown by a margin on each side (negative shrinks):
 * `(minX - marginX, maxX + marginX, minY - marginY, maxY + marginY)`; `contains(grow(b, mx, my), x, y)`
 * is the same test as `contains(b, x, y, mx, my)`. Returns a new box. Mirrors C# `Aabb2D.Grow`. */
export function grow(box: Aabb2DLike, marginX: number, marginY: number): Aabb2DLike {
  return { minX: box.minX - marginX, maxX: box.maxX + marginX, minY: box.minY - marginY, maxY: box.maxY + marginY };
}

/** Whether two boxes overlap (touching edges count). Mirrors C# `Aabb2D.Intersects`. */
export function intersects(a: Aabb2DLike, b: Aabb2DLike): boolean {
  return a.minX <= b.maxX && a.maxX >= b.minX && a.minY <= b.maxY && a.maxY >= b.minY;
}
