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

/** What a facing frame reads from its body (a planck `Body` has all of it). The C# twin takes an
 * `IPhysxBody2D`. */
export type FacingBody = PositionedBody &
  Pick<Body2DLike, 'getLocalPoint' | 'getWorldVector'> & { getLocalVector(worldVector: Vec2Like): Vec2Like };

/**
 * A box-shaped body that faces left or right, as a cheap view (nothing cached; build one when
 * needed). Mirrors the C# readonly struct `FacingFrame2D`. For a contact or surface frame (normal +
 * tangent, no body) use {@link NormalFrame2D} instead.
 * @example
 * ```ts
 * const f = new FacingFrame2D(body, facing, 1.2, 0.5);
 * if (f.zoneAt(contactPoint, 1.5) === 'top') onRoofHit();
 * body.setTransform(body.getPosition(), f.aimRotationToward(stick));
 * ```
 */
export class FacingFrame2D {
  /** The body viewed. C# `FacingFrame2D.Body`. */
  readonly body: FacingBody;
  /** +1 (or any > 0): nose on local +x; otherwise nose on local -x. C# `FacingFrame2D.Facing` (int). */
  readonly facing: number;
  /** Half the box length along local x. C# `FacingFrame2D.HalfWidth`. */
  readonly halfWidth: number;
  /** Half the box height along local y. C# `FacingFrame2D.HalfHeight`. */
  readonly halfHeight: number;

  /** Mirrors C# `new FacingFrame2D(body, facing, halfWidth, halfHeight)`. Stores the arguments only. */
  constructor(body: FacingBody, facing: number, halfWidth: number, halfHeight: number) {
    this.body = body;
    this.facing = facing;
    this.halfWidth = halfWidth;
    this.halfHeight = halfHeight;
  }

  /** Nose direction in the world: `body.getWorldVector({ x: facing, y: 0 })` (unit when `facing` is ±1).
   * C# `FacingFrame2D.Nose`. */
  get nose(): Vec2Like {
    const v = this.body.getWorldVector({ x: this.facing, y: 0 });
    return { x: v.x, y: v.y };
  }

  /** Roof direction: `upDirection(body)` = `body.getWorldVector({ x: 0, y: 1 })`. C# `FacingFrame2D.Up`. */
  get up(): Vec2Like {
    return upDirection(this.body);
  }

  /** Front-center point of the box: `(p.x + nose.x * halfWidth, p.y + nose.y * halfWidth)` (from the
   * body origin). C# `FacingFrame2D.NoseTip`. */
  get noseTip(): Vec2Like {
    const p = this.body.getPosition();
    const nose = this.nose;
    return { x: p.x + nose.x * this.halfWidth, y: p.y + nose.y * this.halfWidth };
  }

  /** Is the world point on the nose side of the body's center? `body.getLocalPoint(worldPoint).x * facing > 0`.
   * C# `FacingFrame2D.IsInFront`. */
  isInFront(worldPoint: Vec2Like): boolean {
    return this.body.getLocalPoint(worldPoint).x * this.facing > 0;
  }

  /** How well the nose lines up with `direction` (1 = along it for a unit direction, -1 = against):
   * `nose.x * direction.x + nose.y * direction.y`. C# `FacingFrame2D.NoseAlignment`. */
  noseAlignment(direction: Vec2Like): number {
    const nose = this.nose;
    return nose.x * direction.x + nose.y * direction.y;
  }

  /** Side of the body the world point is on:
   * `l = getLocalPoint(p); classifyBoxSide({ x: l.x * facing, y: l.y }, halfWidth, halfHeight, axisBias)`.
   * `axisBias` > 1 favors front / back on corners. C# `FacingFrame2D.ZoneAt`. */
  zoneAt(worldPoint: Vec2Like, axisBias: number): BoxSide2D {
    const l = this.body.getLocalPoint(worldPoint);
    return classifyBoxSide({ x: l.x * this.facing, y: l.y }, this.halfWidth, this.halfHeight, axisBias);
  }

  /** Side of the body facing the world direction:
   * `l = getLocalVector(d); classifyBoxSide({ x: l.x * facing, y: l.y }, halfWidth, halfHeight, axisBias)`
   * (e.g. pass `-normal` of a surface it hit to get the side that hit). C# `FacingFrame2D.ZoneToward`. */
  zoneToward(worldDirection: Vec2Like, axisBias: number): BoxSide2D {
    const l = this.body.getLocalVector(worldDirection);
    return classifyBoxSide({ x: l.x * this.facing, y: l.y }, this.halfWidth, this.halfHeight, axisBias);
  }

  /** Bottom corners, back then front: `c.x + nose.x * halfWidth * end - up.x * halfHeight` (and y)
   * for `end` = -1, then +1 (e.g. to cast rays down from both ends). C# `FacingFrame2D.UndersideEnds`
   * (groups `halfWidth * end` first; identical bits since `end` is ±1). */
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
   * i.e. `Math.atan2(d.y, d.x) - (facing > 0 ? 0 : Math.PI)` (not wrapped). C# `FacingFrame2D.AimRotationToward`. */
  aimRotationToward(direction: Vec2Like): number {
    return angleAligningForward(direction, this.facing);
  }
}
