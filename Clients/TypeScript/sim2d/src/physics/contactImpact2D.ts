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

/**
 * Immutable snapshot of one body (the striker) hitting another (the target) at a contact point,
 * with the derived closing / tangential speeds. Mirrors the C# readonly struct `ContactImpact2D`.
 * Pure: reading it never touches a body; apply the response with {@link BodyMotion2D}.
 * @example
 * ```ts
 * router.onPreSolve(isStriker, isTarget, (c) => {
 *   const hit = ContactImpact2D.capture(c.point, c.normal, c.bodyA, c.bodyB);
 *   if (hit.closingSpeed > 0) BodyMotion2D.setVelocityInFrame(c.bodyB, hit.frame, hit.closingSpeed * k, hit.targetTangential);
 * });
 * ```
 */
export class ContactImpact2D {
  /** Contact point in world space (a copy). C# `ContactImpact2D.Point`. */
  readonly point: Vec2Like;
  /** Unit normal from the striker into the target (a copy). C# `ContactImpact2D.Normal`. */
  readonly normal: Vec2Like;
  /** Striker's linear velocity at capture (a copy). C# `ContactImpact2D.StrikerVelocity`. */
  readonly strikerVelocity: Vec2Like;
  /** Striker's angular velocity at capture (rad/s, CCW). C# `ContactImpact2D.StrikerSpin`. */
  readonly strikerSpin: number;
  /** Striker's center of mass at capture (a copy). C# `ContactImpact2D.StrikerCenter`. */
  readonly strikerCenter: Vec2Like;
  /** Target's linear velocity at capture (a copy). C# `ContactImpact2D.TargetVelocity`. */
  readonly targetVelocity: Vec2Like;
  /** Velocity of the striker's material at the contact point:
   * `pointVelocity(strikerVelocity, strikerSpin, point - strikerCenter)`. C# `ContactImpact2D.StrikerPointVelocity`. */
  readonly strikerPointVelocity: Vec2Like;
  /** `NormalFrame2D.left(normal)`: tangent `(-n.y, n.x)`. C# `ContactImpact2D.Frame`. */
  readonly frame: NormalFrame2D;

  /** Copies every vector and derives {@link strikerPointVelocity} and {@link frame}. Mirrors the C#
   * `ContactImpact2D` constructor. Prefer {@link ContactImpact2D.capture} when you hold the bodies. */
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

  /** Reads the bodies at a contact (pre-solve, before the solver changes velocities): `(v, w, worldCenter)`
   * of the striker and `v` of the target. Mirrors C# `ContactImpact2D.Capture`. */
  static capture(
    point: Vec2Like,
    normal: Vec2Like,
    striker: LinearBody & AngularBody & Pick<Body2DLike, 'getWorldCenter'>,
    target: LinearBody,
  ): ContactImpact2D {
    return new ContactImpact2D(point, normal, striker.getLinearVelocity(), striker.getAngularVelocity(), striker.getWorldCenter(), target.getLinearVelocity());
  }

  /** Relative speed along the normal, striker point minus target (positive = closing in):
   * `(cp.x - vb.x) * n.x + (cp.y - vb.y) * n.y` (the difference first). C# `ContactImpact2D.ClosingSpeed`. */
  get closingSpeed(): number {
    const cp = this.strikerPointVelocity;
    return (cp.x - this.targetVelocity.x) * this.normal.x + (cp.y - this.targetVelocity.y) * this.normal.y;
  }

  /** Striker point's speed into the target (along the normal): `cp.x * n.x + cp.y * n.y`. C#
   * `ContactImpact2D.StrikerInto`. */
  get strikerInto(): number {
    return this.strikerPointVelocity.x * this.normal.x + this.strikerPointVelocity.y * this.normal.y;
  }

  /** Target's speed: `Math.sqrt(vb.x * vb.x + vb.y * vb.y)`. C# `ContactImpact2D.TargetSpeed`
   * (`Vector2.Length` there). */
  get targetSpeed(): number {
    return Math.sqrt(this.targetVelocity.x * this.targetVelocity.x + this.targetVelocity.y * this.targetVelocity.y);
  }

  /** Target velocity along the tangent: `frame.across(targetVelocity)`. C# `ContactImpact2D.TargetTangential`. */
  get targetTangential(): number {
    return this.frame.across(this.targetVelocity);
  }

  /** Striker point velocity along the tangent: `frame.across(strikerPointVelocity)`. C#
   * `ContactImpact2D.StrikerTangential`. */
  get strikerTangential(): number {
    return this.frame.across(this.strikerPointVelocity);
  }

  /** Tangential speed difference at the contact (feed {@link BodyMotion2D.addSpinFromSlip}):
   * `strikerTangential - targetTangential`. C# `ContactImpact2D.Slip`. */
  get slip(): number {
    return this.strikerTangential - this.targetTangential;
  }
}
