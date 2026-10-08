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
 * (clockwise; +x on a floor). C#: `TangentSide2D`. */
export type TangentSide2D = 'left' | 'right';

export class NormalFrame2D {
  /** The unit normal. */
  readonly normal: Vec2Like;
  /** `(-n.y, n.x)` for `'left'`, `(n.y, -n.x)` for `'right'`. */
  readonly tangent: Vec2Like;

  constructor(normal: Vec2Like, side: TangentSide2D) {
    this.normal = { x: normal.x, y: normal.y };
    this.tangent = side === 'left' ? { x: -normal.y, y: normal.x } : { x: normal.y, y: -normal.x };
  }

  /** Tangent `(-n.y, n.x)`. */
  static left(normal: Vec2Like): NormalFrame2D {
    return new NormalFrame2D(normal, 'left');
  }

  /** Tangent `(n.y, -n.x)`. */
  static right(normal: Vec2Like): NormalFrame2D {
    return new NormalFrame2D(normal, 'right');
  }

  /** Component along the normal: `v.x * n.x + v.y * n.y`. */
  along(v: Vec2Like): number {
    return v.x * this.normal.x + v.y * this.normal.y;
  }

  /** Component along the tangent: `v.x * t.x + v.y * t.y`. */
  across(v: Vec2Like): number {
    return v.x * this.tangent.x + v.y * this.tangent.y;
  }

  /** Speed into the surface (positive = toward it): `-(v.x * n.x + v.y * n.y)`. */
  into(v: Vec2Like): number {
    return -(v.x * this.normal.x + v.y * this.normal.y);
  }

  /** `(n.x * alongNormal + t.x * alongTangent, n.y * alongNormal + t.y * alongTangent)`. */
  compose(alongNormal: number, alongTangent: number): Vec2Like {
    return {
      x: this.normal.x * alongNormal + this.tangent.x * alongTangent,
      y: this.normal.y * alongNormal + this.tangent.y * alongTangent,
    };
  }

  /** The tangent turned to the side `direction` points to (`fallbackSign` when square to the surface):
   * `side = signOr(across(direction), fallbackSign); (t.x * side, t.y * side)`. */
  tangentToward(direction: Vec2Like, fallbackSign: number): Vec2Like {
    const side = signOr(this.across(direction), fallbackSign);
    return { x: this.tangent.x * side, y: this.tangent.y * side };
  }

  /** Distance of `point` from the surface through `surfacePoint`, along the normal:
   * `(p.x - s.x) * n.x + (p.y - s.y) * n.y`. */
  heightOf(point: Vec2Like, surfacePoint: Vec2Like): number {
    return (point.x - surfacePoint.x) * this.normal.x + (point.y - surfacePoint.y) * this.normal.y;
  }
}
