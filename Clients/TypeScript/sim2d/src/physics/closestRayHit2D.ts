/**
 * Physics layer. Closest accepted ray hit — mirror of C# `Altruist.Physx.TwoD.ClosestRayHit2D`
 * and `RayCastExtensions2D.RayCastClosest`.
 */
import type { Vec2Like } from '../math/vec2.ts';
import type { RayCastWorld } from './body2D.ts';

/** Reusable callback: rejected fixtures are ignored (-1), accepted hits clip the ray and are kept
 * when `fraction * length` is below the best so far. Cast several rays into one instance to get the
 * closest over all of them. Pass `hit.onHit` (bound) as the world's callback. */
export class ClosestRayHit2D<F = unknown> {
  found = false;
  fixture: F | null = null;
  point: Vec2Like = { x: 0, y: 0 };
  normal: Vec2Like = { x: 0, y: 0 };
  fraction = 0;
  /** `fraction * length` of the kept hit; Infinity when none. */
  distance = Infinity;

  length: number;
  filter: ((fixture: F) => boolean) | null;

  constructor(length: number, filter: ((fixture: F) => boolean) | null = null) {
    this.length = length;
    this.filter = filter;
  }

  reset(): void {
    this.found = false;
    this.fixture = null;
    this.point = { x: 0, y: 0 };
    this.normal = { x: 0, y: 0 };
    this.fraction = 0;
    this.distance = Infinity;
  }

  readonly onHit = (fixture: F, point: Vec2Like, normal: Vec2Like, fraction: number): number => {
    if (this.filter && !this.filter(fixture)) return -1;
    const dist = fraction * this.length;
    if (dist < this.distance) {
      this.distance = dist;
      this.found = true;
      this.fixture = fixture;
      this.point = { x: point.x, y: point.y };
      this.normal = { x: normal.x, y: normal.y };
      this.fraction = fraction;
    }
    return fraction;
  };

  /** Casts one ray into this instance. */
  cast(world: RayCastWorld<F>, from: Vec2Like, to: Vec2Like): this {
    world.rayCast(from, to, this.onHit);
    return this;
  }
}

/** Casts from `from` to `to` and returns the closest accepted hit; distances use `length`, or the
 * ray's length when omitted. */
export function rayCastClosest<F>(
  world: RayCastWorld<F>,
  from: Vec2Like,
  to: Vec2Like,
  filter: ((fixture: F) => boolean) | null = null,
  length?: number,
): ClosestRayHit2D<F> {
  const len = length ?? Math.sqrt((to.x - from.x) * (to.x - from.x) + (to.y - from.y) * (to.y - from.y));
  return new ClosestRayHit2D<F>(len, filter).cast(world, from, to);
}
