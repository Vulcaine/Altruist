/**
 * Gameplay layer. A box-shaped body that faces left or right — mirror of C#
 * `Altruist.Gaming.TwoD.FacingFrame2D`: nose and roof directions, nose tip, which side something
 * touched, in front or not, underside corners, the rotation that aims the nose. A view over the body
 * (nothing cached). `facing` > 0 puts the nose on local +x, otherwise on local -x; the roof is +y.
 * Each member calls the body's own `getWorldVector` / `getLocalPoint` / `getLocalVector` (planck's).
 */
import { classifyBoxSide, type BoxSide2D } from '../math/geometry2D.ts';
import { angleAligningForward } from '../math/rotation2D.ts';
import type { Vec2Like } from '../math/vec2.ts';
import type { Body2DLike, PositionedBody } from '../physics/body2D.ts';
import { upDirection } from './gameplayVerbs2D.ts';

/** What a facing frame reads from its body (a planck `Body` has all of it). */
export type FacingBody = PositionedBody &
  Pick<Body2DLike, 'getLocalPoint' | 'getWorldVector'> & { getLocalVector(worldVector: Vec2Like): Vec2Like };

export class FacingFrame2D {
  readonly body: FacingBody;
  readonly facing: number;
  readonly halfWidth: number;
  readonly halfHeight: number;

  constructor(body: FacingBody, facing: number, halfWidth: number, halfHeight: number) {
    this.body = body;
    this.facing = facing;
    this.halfWidth = halfWidth;
    this.halfHeight = halfHeight;
  }

  /** `body.getWorldVector({ x: facing, y: 0 })`. */
  get nose(): Vec2Like {
    const v = this.body.getWorldVector({ x: this.facing, y: 0 });
    return { x: v.x, y: v.y };
  }

  /** `upDirection(body)` = `body.getWorldVector({ x: 0, y: 1 })`. */
  get up(): Vec2Like {
    return upDirection(this.body);
  }

  /** `(p.x + nose.x * halfWidth, p.y + nose.y * halfWidth)`. */
  get noseTip(): Vec2Like {
    const p = this.body.getPosition();
    const nose = this.nose;
    return { x: p.x + nose.x * this.halfWidth, y: p.y + nose.y * this.halfWidth };
  }

  /** `body.getLocalPoint(worldPoint).x * facing > 0`. */
  isInFront(worldPoint: Vec2Like): boolean {
    return this.body.getLocalPoint(worldPoint).x * this.facing > 0;
  }

  /** `nose.x * direction.x + nose.y * direction.y`. */
  noseAlignment(direction: Vec2Like): number {
    const nose = this.nose;
    return nose.x * direction.x + nose.y * direction.y;
  }

  /** Side of the body the world point is on:
   * `l = getLocalPoint(p); classifyBoxSide({ x: l.x * facing, y: l.y }, halfWidth, halfHeight, axisBias)`. */
  zoneAt(worldPoint: Vec2Like, axisBias: number): BoxSide2D {
    const l = this.body.getLocalPoint(worldPoint);
    return classifyBoxSide({ x: l.x * this.facing, y: l.y }, this.halfWidth, this.halfHeight, axisBias);
  }

  /** Side of the body facing the world direction:
   * `l = getLocalVector(d); classifyBoxSide({ x: l.x * facing, y: l.y }, halfWidth, halfHeight, axisBias)`. */
  zoneToward(worldDirection: Vec2Like, axisBias: number): BoxSide2D {
    const l = this.body.getLocalVector(worldDirection);
    return classifyBoxSide({ x: l.x * this.facing, y: l.y }, this.halfWidth, this.halfHeight, axisBias);
  }

  /** Bottom corners, back then front: `c.x + nose.x * halfWidth * end - up.x * halfHeight` (and y)
   * for `end` = -1, then +1. */
  undersideEnds(): { back: Vec2Like; front: Vec2Like } {
    const up = this.up;
    const nose = this.nose;
    const c = this.body.getPosition();
    const at = (end: number): Vec2Like => ({
      x: c.x + nose.x * this.halfWidth * end - up.x * this.halfHeight,
      y: c.y + nose.y * this.halfWidth * end - up.y * this.halfHeight,
    });
    return { back: at(-1), front: at(1) };
  }

  /** Body rotation pointing the nose along `direction`: `angleAligningForward(direction, facing)`,
   * i.e. `Math.atan2(d.y, d.x) - (facing > 0 ? 0 : Math.PI)`. */
  aimRotationToward(direction: Vec2Like): number {
    return angleAligningForward(direction, this.facing);
  }
}
