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

function distanceSquared(a: Vec2Like, b: Vec2Like): number {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  return dx * dx + dy * dy;
}
