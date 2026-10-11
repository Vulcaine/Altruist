/**
 * Physics layer. Sum of a body's contact normals with support (ground) classification — mirror of
 * C# `Altruist.Physx.TwoD.ContactNormals2D`. Directions divide by `Math.sqrt(x * x + y * y)` like C#
 * (not `Math.hypot`).
 */
import type { Vec2Like } from '../math/vec2.ts';

/**
 * Accumulator of a body's contact normals that also sums the "support" (ground-like) ones. Mirrors
 * the C# mutable struct `ContactNormals2D` (fields `All` / `Support` are split into x / y here).
 * Create one per query; it never touches a body.
 * @example
 * ```ts
 * const normals = new ContactNormals2D();
 * const up = body.getWorldVector({ x: 0, y: 1 });
 * touchingContactsOf(body, isSurface, (c) => normals.add(c.normal, up, 0.7)); // normal: surface → body
 * if (normals.hasSupport) groundNormal = normals.supportDirection;
 * ```
 */
export class ContactNormals2D {
  /** X of the sum of every added normal. */
  allX = 0;
  /** Y of the sum of every added normal. */
  allY = 0;
  /** True once any normal was added ({@link all} may still sum to zero). C# `HasAny`. */
  hasAny = false;
  /** X of the sum of the supporting normals. */
  supportX = 0;
  /** Y of the sum of the supporting normals. */
  supportY = 0;
  /** True once a supporting normal was added. C# `HasSupport`. */
  hasSupport = false;

  /** Adds a normal (out of the surface toward the body); it is support when `n·up >= minUpDot`.
   * Without `up` the normal is not tested for support. Mirrors both C# overloads
   * `ContactNormals2D.Add(normal, up, minUpDot)` and `Add(normal)`.
   * @param normal - Unit contact normal pointing out of the surface toward the body.
   * @param up - The body's unit up (e.g. `getWorldVector({ x: 0, y: 1 })`) or world +Y.
   * @param minUpDot - Cosine of the widest support angle (e.g. 0.7 ≈ 45°); default 0. */
  add(normal: Vec2Like, up?: Vec2Like, minUpDot = 0): void {
    this.hasAny = true;
    this.allX += normal.x;
    this.allY += normal.y;
    if (up && normal.x * up.x + normal.y * up.y >= minUpDot) {
      this.hasSupport = true;
      this.supportX += normal.x;
      this.supportY += normal.y;
    }
  }

  /** Sum of every added normal, as a new object. C# `ContactNormals2D.All`. */
  get all(): Vec2Like {
    return { x: this.allX, y: this.allY };
  }

  /** Sum of the supporting normals, as a new object. C# `ContactNormals2D.Support`. */
  get support(): Vec2Like {
    return { x: this.supportX, y: this.supportY };
  }

  /** Average direction of all normals, `sum / (|sum| || 1)` with `Math.sqrt` (zero when none).
   * C# `ContactNormals2D.AllDirection`. */
  get allDirection(): Vec2Like {
    return direction(this.allX, this.allY);
  }

  /** Average direction of the supporting normals, `sum / (|sum| || 1)` (zero when none). C#
   * `ContactNormals2D.SupportDirection`. */
  get supportDirection(): Vec2Like {
    return direction(this.supportX, this.supportY);
  }
}

function direction(x: number, y: number): Vec2Like {
  const l = Math.sqrt(x * x + y * y) || 1;
  return { x: x / l, y: y / l };
}
