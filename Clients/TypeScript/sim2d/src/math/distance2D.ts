/**
 * Math layer. Distances — mirror of C# `Altruist.TwoD.Numerics.Distance2D`.
 * Euclidean lengths live in `VectorMath2D.length`, `Math.sqrt(x * x + y * y)` like C# (not
 * `Math.hypot`, which can differ in the last bit).
 */
import type { Vec2Like } from './vec2.ts';

/** Euclidean distance between two points: `Math.sqrt(dx * dx + dy * dy)`. Mirrors C# `Distance2D.Between`. */
export function between(a: Vec2Like, b: Vec2Like): number {
  return Math.sqrt(squared(a, b));
}

/** Squared distance between two points: `dx * dx + dy * dy`. Mirrors C# `Distance2D.Squared`. */
export function squared(a: Vec2Like, b: Vec2Like): number {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  return dx * dx + dy * dy;
}

/** Manhattan distance with the vertical part weighted: `Math.abs(dx) + Math.abs(dy) * weightY`
 * (`weightY` > 1 makes height differences count more). Mirrors C# `Distance2D.Weighted`. For a
 * Euclidean length use {@link VectorMath2D.lengthXY}. */
export function weighted(dx: number, dy: number, weightY: number): number {
  return Math.abs(dx) + Math.abs(dy) * weightY;
}

/** {@link weighted} between two points: `Math.abs(a.x - b.x) + Math.abs(a.y - b.y) * weightY`.
 * Mirrors C# `Distance2D.WeightedBetween`. */
export function weightedBetween(a: Vec2Like, b: Vec2Like, weightY: number): number {
  return Math.abs(a.x - b.x) + Math.abs(a.y - b.y) * weightY;
}
