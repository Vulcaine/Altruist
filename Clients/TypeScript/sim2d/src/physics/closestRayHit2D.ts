/**
 * Physics layer. Closest accepted ray hit — mirror of C# `Altruist.Physx.TwoD.ClosestRayHit2D`
 * and `RayCastExtensions2D.RayCastClosest`. The default ray length is `Math.sqrt(dx * dx + dy * dy)`
 * like C# (not `Math.hypot`).
 */
import type { Vec2Like } from '../math/vec2.ts';
import type { RayCastWorld } from './body2D.ts';

/** Reusable callback: rejected fixtures are ignored (-1), accepted hits clip the ray and are kept
 * when `fraction * length` is below the best so far. Cast several rays into one instance to get the
 * closest over all of them. Pass `hit.onHit` (bound) as the world's callback. Mirrors C#
 * `ClosestRayHit2D` (an `IPhysxRayCastCallback2D`). For a one-off cast use {@link rayCastClosest}.
 * @typeParam F - The engine's fixture type.
 * @example
 * ```ts
 * const hit = new ClosestRayHit2D<Fixture>(rayLength, (f) => isSolid(f));
 * hit.cast(world, a, a2).cast(world, b, b2); // closest over both rays
 * if (hit.found) groundY = hit.point.y;
 * ```
 */
export class ClosestRayHit2D<F = unknown> {
  /** Whether any accepted hit was kept. C# `ClosestRayHit2D.Found`. */
  found = false;
  /** Fixture of the kept hit, or null. C# `ClosestRayHit2D.Fixture`. */
  fixture: F | null = null;
  /** World point of the kept hit (a copy). C# `ClosestRayHit2D.Point`. */
  point: Vec2Like = { x: 0, y: 0 };
  /** Surface normal at the kept hit (a copy). C# `ClosestRayHit2D.Normal`. */
  normal: Vec2Like = { x: 0, y: 0 };
  /** Ray fraction (0..1 of the cast segment) of the kept hit. C# `ClosestRayHit2D.Fraction`. */
  fraction = 0;
  /** `fraction * length` of the kept hit; Infinity when none. */
  distance = Infinity;

  /** Distance scale for fractions (`distance = fraction * length`); normally the ray's length. C#
   * `ClosestRayHit2D.Length`. */
  length: number;
  /** Accepts a fixture (null accepts all); rejected fixtures are ignored by the cast. C#
   * `ClosestRayHit2D.Filter`. */
  filter: ((fixture: F) => boolean) | null;

  /**
   * Mirrors C# `new ClosestRayHit2D(length, filter)`.
   * @param length - Distance scale for fractions.
   * @param filter - Fixture filter; null (default) accepts all.
   */
  constructor(length: number, filter: ((fixture: F) => boolean) | null = null) {
    this.length = length;
    this.filter = filter;
  }

  /** Clears the kept hit (`found` false, `distance` Infinity); keeps `length` and `filter`. Mirrors
   * C# `ClosestRayHit2D.Reset`. */
  reset(): void {
    this.found = false;
    this.fixture = null;
    this.point = { x: 0, y: 0 };
    this.normal = { x: 0, y: 0 };
    this.fraction = 0;
    this.distance = Infinity;
  }

  /** The ray-cast callback (an arrow function, safe to pass unbound): -1 for a filtered-out fixture,
   * otherwise keeps the hit when `fraction * length` beats the best so far and returns `fraction`
   * (clips the ray). Mirrors C# `ClosestRayHit2D.OnHit`. */
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

  /** Casts one ray into this instance (does not reset first, so several casts keep the closest over
   * all). @returns this, for chaining. */
  cast(world: RayCastWorld<F>, from: Vec2Like, to: Vec2Like): this {
    world.rayCast(from, to, this.onHit);
    return this;
  }
}

/** Casts from `from` to `to` and returns the closest accepted hit; distances use `length`, or the
 * ray's length (`Math.sqrt`) when omitted. Allocates one {@link ClosestRayHit2D} per call; reuse an
 * instance (and `reset()`) in hot paths. Check `found` on the result. Mirrors C#
 * `RayCastExtensions2D.RayCastClosest`.
 * @param filter - Accepts a fixture (null accepts all).
 * @param length - Distance scale for fractions; pass a precomputed value to match other code bit for bit. */
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
