/**
 * Math layer. The frame of a contact or a surface — mirror of C# `Altruist.TwoD.Numerics.NormalFrame2D`.
 * A unit normal and the tangent on the chosen side; splits a vector into "along the normal" and
 * "along the surface" and builds one back.
 *
 * Dot products are the scalar form `v.x * n.x + v.y * n.y` (C# uses `Vector2.Dot`, whose only
 * difference from that form is the sign of a zero result).
 */
import { signOr } from './scalar.ts';
import type { Vec2Like } from './vec2.ts';

/** `'left'`: tangent `(-n.y, n.x)` (counter-clockwise of the normal); `'right'`: `(n.y, -n.x)`
 * (clockwise; +x on a floor). Mirrors the C# enum `TangentSide2D` (`Left`, `Right`). */
export type TangentSide2D = 'left' | 'right';

/**
 * Immutable normal/tangent frame of a contact or surface. Mirrors the C# readonly struct
 * `NormalFrame2D`. Pure math: it never touches a body. For the contact normal of a live physics
 * contact see {@link ContactNormals2D}; for a body-facing frame see {@link FacingFrame2D}.
 * @example
 * ```ts
 * const f = NormalFrame2D.right({ x: 0, y: 1 }); // floor: tangent = +x
 * const v2 = f.compose(Math.max(0, f.along(v)), f.across(v)); // drop speed into the floor
 * ```
 */
export class NormalFrame2D {
  /** The unit normal (copied from the constructor argument; not re-normalized). */
  readonly normal: Vec2Like;
  /** `(-n.y, n.x)` for `'left'`, `(n.y, -n.x)` for `'right'`. */
  readonly tangent: Vec2Like;

  /**
   * Builds the frame. Mirrors C# `new NormalFrame2D(normal, side)`.
   * @param normal - Unit normal; copied, not normalized.
   * @param side - Which perpendicular becomes the tangent.
   */
  constructor(normal: Vec2Like, side: TangentSide2D) {
    this.normal = { x: normal.x, y: normal.y };
    this.tangent = side === 'left' ? { x: -normal.y, y: normal.x } : { x: normal.y, y: -normal.x };
  }

  /** Frame with the counter-clockwise tangent `(-n.y, n.x)`. Mirrors C# `NormalFrame2D.Left`. */
  static left(normal: Vec2Like): NormalFrame2D {
    return new NormalFrame2D(normal, 'left');
  }

  /** Frame with the clockwise tangent `(n.y, -n.x)` (+x for a floor normal). Mirrors C#
   * `NormalFrame2D.Right`. */
  static right(normal: Vec2Like): NormalFrame2D {
    return new NormalFrame2D(normal, 'right');
  }

  /** Component along the normal (positive = away from the surface): `v.x * n.x + v.y * n.y`.
   * Mirrors C# `NormalFrame2D.Along`. */
  along(v: Vec2Like): number {
    return v.x * this.normal.x + v.y * this.normal.y;
  }

  /** Component along the tangent: `v.x * t.x + v.y * t.y`. Mirrors C# `NormalFrame2D.Across`. */
  across(v: Vec2Like): number {
    return v.x * this.tangent.x + v.y * this.tangent.y;
  }

  /** Speed into the surface (positive = toward it): `-(v.x * n.x + v.y * n.y)`, i.e. `-along(v)`.
   * Mirrors C# `NormalFrame2D.Into`. */
  into(v: Vec2Like): number {
    return -(v.x * this.normal.x + v.y * this.normal.y);
  }

  /** Builds a vector from its normal and tangent components (inverse of {@link along} /
   * {@link across}): `(n.x * alongNormal + t.x * alongTangent, n.y * alongNormal + t.y * alongTangent)`.
   * Mirrors C# `NormalFrame2D.Compose`. */
  compose(alongNormal: number, alongTangent: number): Vec2Like {
    return {
      x: this.normal.x * alongNormal + this.tangent.x * alongTangent,
      y: this.normal.y * alongNormal + this.tangent.y * alongTangent,
    };
  }

  /** The tangent turned to the side `direction` points to (`fallbackSign` when square to the surface):
   * `side = signOr(across(direction), fallbackSign); (t.x * side, t.y * side)`. Mirrors C#
   * `NormalFrame2D.TangentToward`. */
  tangentToward(direction: Vec2Like, fallbackSign: number): Vec2Like {
    const side = signOr(this.across(direction), fallbackSign);
    return { x: this.tangent.x * side, y: this.tangent.y * side };
  }

  /** Distance of `point` from the surface through `surfacePoint`, along the normal:
   * `(p.x - s.x) * n.x + (p.y - s.y) * n.y` (positive on the normal's side). Mirrors C#
   * `NormalFrame2D.HeightOf`. */
  heightOf(point: Vec2Like, surfacePoint: Vec2Like): number {
    return (point.x - surfacePoint.x) * this.normal.x + (point.y - surfacePoint.y) * this.normal.y;
  }
}
