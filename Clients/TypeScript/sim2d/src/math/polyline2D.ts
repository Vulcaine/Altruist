/**
 * Math layer. Polylines from lines and arcs — mirror of C# `Altruist.TwoD.Numerics.Polyline2D`.
 */
import { toRadians } from './angle.ts';
import type { Vec2Like } from './vec2.ts';

/** Appends an arc as `steps + 1` points:
 * `steps = max(minSegments, ceil(|end - start| / maxSegmentDegrees))`,
 * `a = toRadians(start + ((end - start) * i) / steps)`, `(cx + r * cos a, cy + r * sin a)`.
 * Angles in degrees, counter-clockwise from +X; both endpoints are included, so chaining arcs
 * duplicates the joint (clean up with {@link removeDuplicates}). Mutates `points`. Mirrors C#
 * `Polyline2D.AppendArc`. */
export function appendArc(
  points: Vec2Like[],
  centerX: number,
  centerY: number,
  radius: number,
  startDegrees: number,
  endDegrees: number,
  maxSegmentDegrees: number,
  minSegments = 2,
): void {
  const a0 = startDegrees;
  const a1 = endDegrees;
  const steps = Math.max(minSegments, Math.ceil(Math.abs(a1 - a0) / maxSegmentDegrees));
  for (let i = 0; i <= steps; i++) {
    const a = toRadians(a0 + ((a1 - a0) * i) / steps);
    points.push({ x: centerX + radius * Math.cos(a), y: centerY + radius * Math.sin(a) });
  }
}

/** Drops consecutive points within `sqrt(epsilonSquared)` (squared distance not above it) and, when
 * `closed`, a last point that coincides with the first (squared distance below it). Returns a new
 * array; input points are reused, not copied. Mirrors C# `Polyline2D.RemoveDuplicates`. */
export function removeDuplicates(points: readonly Vec2Like[], epsilonSquared = 1e-10, closed = true): Vec2Like[] {
  const out: Vec2Like[] = [];
  for (const p of points) {
    const last = out[out.length - 1];
    if (!last || distanceSquared(last, p) > epsilonSquared) out.push(p);
  }
  const first = out[0];
  const last = out[out.length - 1];
  if (closed && first && last && out.length > 1 && distanceSquared(first, last) < epsilonSquared) out.pop();
  return out;
}

/** The index of the point of `points` nearest `point` and its distance, searching `window` indices
 * either side of `last` (so a line that crosses itself never jumps to the other part), wrapping
 * round a `closed` line (its last point repeats its first), or every point when `last` is
 * negative. Ties keep the lower searched index. Mirrors C# `Polyline2D.NearestIndex`. */
export function nearestIndex(points: readonly Vec2Like[], point: Vec2Like, last: number, window: number, closed: boolean): { index: number; distance: number } {
  const all = last < 0;
  const n = closed ? points.length - 1 : points.length;
  const lo = all ? 0 : closed ? last - window : Math.max(0, last - window);
  const hi = all ? points.length - 1 : closed ? last + window : Math.min(points.length - 1, last + window);
  let best = Math.max(0, last);
  let bestD = Infinity;
  for (let k = lo; k <= hi; k++) {
    const i = ((k % n) + n) % n;
    const d = distanceSquared(points[i]!, point);
    if (d < bestD) {
      bestD = d;
      best = i;
    }
  }
  return { index: best, distance: Math.sqrt(bestD) };
}

/** Drops the points that lie on the straight line between their neighbours (within `tolerance`
 * of the line from the last kept point to the next point, and between the two), so a straight
 * run is one segment; the first and last points are kept. Returns a new array; input points are
 * reused, not copied. Mirrors C# `Polyline2D.DropCollinear`. */
export function dropCollinear(points: readonly Vec2Like[], tolerance: number): Vec2Like[] {
  if (points.length < 3) return [...points];
  const out: Vec2Like[] = [points[0]!];
  for (let i = 1; i < points.length - 1; i++) {
    const a = out[out.length - 1]!;
    const b = points[i]!;
    const c = points[i + 1]!;
    const ux = c.x - a.x;
    const uy = c.y - a.y;
    const len2 = ux * ux + uy * uy;
    const along = (b.x - a.x) * ux + (b.y - a.y) * uy;
    const off = Math.abs((b.x - a.x) * uy - (b.y - a.y) * ux);
    if (len2 === 0 || along < 0 || along > len2 || off > tolerance * Math.sqrt(len2)) out.push(b);
  }
  out.push(points[points.length - 1]!);
  return out;
}

function distanceSquared(a: Vec2Like, b: Vec2Like): number {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  return dx * dx + dy * dy;
}
