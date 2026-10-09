/**
 * Math layer. Piecewise-linear curves of `[x, y]` points sorted by x — mirror of C#
 * `Altruist.Numerics.PiecewiseLinear`. Segments evaluate `y0 + ((y1 - y0) * (x - x0)) / (x1 - x0)`.
 */
/** One `[x, y]` control point of a curve; points must be sorted by ascending x. Mirrors the C#
 * tuple `(float X, float Y)`. */
export type CurvePoint = readonly [number, number];

/** Y at `x` by linear interpolation, clamped to the first / last y outside the range (0 for an
 * empty curve). Mirrors C# `PiecewiseLinear.Evaluate`. */
export function evaluate(points: readonly CurvePoint[], x: number): number {
  if (points.length === 0) return 0;
  if (x <= points[0]![0]) return points[0]![1];
  for (let i = 1; i < points.length; i++) {
    const [x1, y1] = points[i]!;
    if (x <= x1) {
      const [x0, y0] = points[i - 1]!;
      return y0 + ((y1 - y0) * (x - x0)) / (x1 - x0);
    }
  }
  return points[points.length - 1]![1];
}

/** X at which an increasing curve reaches `y` (inverse of {@link evaluate}): the first x at or
 * below the first y, Infinity above the last y (and for an empty curve). Only meaningful for
 * non-decreasing y; a flat segment divides by zero. Mirrors C# `PiecewiseLinear.Inverse`. */
export function inverse(points: readonly CurvePoint[], y: number): number {
  if (points.length === 0) return Infinity;
  if (y <= points[0]![1]) return points[0]![0];
  for (let i = 1; i < points.length; i++) {
    const [x1, y1] = points[i]!;
    if (y <= y1) {
      const [x0, y0] = points[i - 1]!;
      return x0 + ((x1 - x0) * (y - y0)) / (y1 - y0);
    }
  }
  return Infinity;
}
