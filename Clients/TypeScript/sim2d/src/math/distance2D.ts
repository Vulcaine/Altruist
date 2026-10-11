/**
 * Math layer. Distances — mirror of C# `Altruist.TwoD.Numerics.Distance2D` (the weighted forms).
 * Euclidean lengths live in `VectorMath2D.length`, `Math.sqrt(x * x + y * y)` like C# (not
 * `Math.hypot`, which can differ in the last bit).
 */
import type { Vec2Like } from './vec2.ts';

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
