/**
 * Physics layer. One body (the striker) hitting another (the target) at a contact point — mirror of
 * C# `Altruist.Physx.TwoD.ContactImpact2D`. Capture it before the solver changes the velocities;
 * build the response with `frame` (`setVelocityInFrame(target, impact.frame, outN, outT)`,
 * `addSpinFromSlip(target, impact.slip, radius, k)`).
 *
 * `normal` points from the striker into the target; the frame's tangent is `(-n.y, n.x)`.
 * Dot products are the scalar form `a.x * b.x + a.y * b.y`; `targetSpeed` is
 * `Math.sqrt(x * x + y * y)` like C# — not `Math.hypot`, which can differ in the last bit.
 */
import { NormalFrame2D } from '../math/normalFrame2D.ts';
import type { Vec2Like } from '../math/vec2.ts';
import type { AngularBody, Body2DLike, LinearBody } from './body2D.ts';
import { pointVelocity } from './velocity2D.ts';

export class ContactImpact2D {
  readonly point: Vec2Like;
  readonly normal: Vec2Like;
  readonly strikerVelocity: Vec2Like;
  readonly strikerSpin: number;
  readonly strikerCenter: Vec2Like;
  readonly targetVelocity: Vec2Like;
  /** `pointVelocity(strikerVelocity, strikerSpin, point - strikerCenter)`. */
  readonly strikerPointVelocity: Vec2Like;
  /** `NormalFrame2D.left(normal)`. */
  readonly frame: NormalFrame2D;

  constructor(point: Vec2Like, normal: Vec2Like, strikerVelocity: Vec2Like, strikerSpin: number, strikerCenter: Vec2Like, targetVelocity: Vec2Like) {
    this.point = { x: point.x, y: point.y };
    this.normal = { x: normal.x, y: normal.y };
    this.strikerVelocity = { x: strikerVelocity.x, y: strikerVelocity.y };
    this.strikerSpin = strikerSpin;
    this.strikerCenter = { x: strikerCenter.x, y: strikerCenter.y };
    this.targetVelocity = { x: targetVelocity.x, y: targetVelocity.y };
    this.strikerPointVelocity = pointVelocity(strikerVelocity, strikerSpin, { x: point.x - strikerCenter.x, y: point.y - strikerCenter.y });
    this.frame = NormalFrame2D.left(normal);
  }

  /** Reads the bodies at a contact (pre-solve): `(v, w, worldCenter)` of the striker and `v` of the target. */
  static capture(
    point: Vec2Like,
    normal: Vec2Like,
    striker: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>,
    target: LinearBody,
  ): ContactImpact2D {
    return new ContactImpact2D(point, normal, striker.getLinearVelocity(), striker.getAngularVelocity(), striker.getWorldCenter(), target.getLinearVelocity());
  }

  /** `(cp.x - vb.x) * n.x + (cp.y - vb.y) * n.y` (the difference first). */
  get closingSpeed(): number {
    const cp = this.strikerPointVelocity;
    return (cp.x - this.targetVelocity.x) * this.normal.x + (cp.y - this.targetVelocity.y) * this.normal.y;
  }

  /** `cp.x * n.x + cp.y * n.y`. */
  get strikerInto(): number {
    return this.strikerPointVelocity.x * this.normal.x + this.strikerPointVelocity.y * this.normal.y;
  }

  /** `Math.sqrt(vb.x * vb.x + vb.y * vb.y)`. */
  get targetSpeed(): number {
    return Math.sqrt(this.targetVelocity.x * this.targetVelocity.x + this.targetVelocity.y * this.targetVelocity.y);
  }

  /** `frame.across(targetVelocity)`. */
  get targetTangential(): number {
    return this.frame.across(this.targetVelocity);
  }

  /** `frame.across(strikerPointVelocity)`. */
  get strikerTangential(): number {
    return this.frame.across(this.strikerPointVelocity);
  }

  /** `strikerTangential - targetTangential`. */
  get slip(): number {
    return this.strikerTangential - this.targetTangential;
  }
}
