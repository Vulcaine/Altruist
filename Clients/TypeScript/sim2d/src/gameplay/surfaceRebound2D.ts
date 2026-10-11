/**
 * Gameplay layer. How a body rebounds when it is driven into a surface (a dash into a wall, a
 * bumper) — mirror of C# `Altruist.Gaming.TwoD.SurfaceRebound2D`. Configure it once per body kind
 * (e.g. a section of the game's config) and apply it with {@link apply}; game code only decides
 * whether it rebounds and by how much more. Same operation order as C#.
 * @example
 * ```ts
 * const speed = Velocity2D.approachSpeed(incoming, normal);
 * if (SurfaceRebound2D.isImpact(rebound, speed) && dashing)
 *   SurfaceRebound2D.apply(rebound, body, normal, SurfaceRebound2D.outSpeed(rebound, speed) * bonus, incoming);
 * ```
 */
import type { Vec2Like } from '../math/vec2.ts';
import type { LinearBody } from '../physics/body2D.ts';
import { bounce } from '../physics/velocity2D.ts';

/** The rebound's settings. C# `SurfaceRebound2D`'s properties. */
export interface SurfaceReboundSettings2D {
  /** Slower approaches (along the normal) are touches, not impacts. */
  readonly minImpactSpeed: number;
  /** Share of the approach speed the body leaves with. */
  readonly restitution: number;
  /** The body always leaves at least this fast. */
  readonly minOutSpeed: number;
  /** Share of the motion along the surface the body keeps. */
  readonly tangentKeep: number;
}

/** Is an approach this fast an impact? `!(approachSpeed < minImpactSpeed)`. Mirrors C# `SurfaceRebound2D.IsImpact`. */
export function isImpact(rebound: SurfaceReboundSettings2D, approachSpeed: number): boolean {
  return !(approachSpeed < rebound.minImpactSpeed);
}

/** The speed the body leaves with: `Math.max(approachSpeed * restitution, minOutSpeed)`. Mirrors C# `SurfaceRebound2D.OutSpeed`. */
export function outSpeed(rebound: SurfaceReboundSettings2D, approachSpeed: number): number {
  return Math.max(approachSpeed * rebound.restitution, rebound.minOutSpeed);
}

/** Rebounds the body from its recorded `incoming` velocity: `outSpeed` out along the unit `normal`,
 * keeping `tangentKeep` of the motion along the surface (`v = Velocity2D.bounce(incoming, normal, outSpeed, tangentKeep)`).
 * Mirrors C# `SurfaceRebound2D.Apply`. */
export function apply(rebound: SurfaceReboundSettings2D, body: LinearBody, normal: Vec2Like, speed: number, incoming: Vec2Like): void {
  body.setLinearVelocity(bounce(incoming, normal, speed, rebound.tangentKeep));
}
