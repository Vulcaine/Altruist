/**
 * Physics layer. Body motion snapshot — mirror of C# `Altruist.Physx.TwoD.BodyState2D`.
 */
import type { Vec2Like } from '../math/vec2.ts';
import type { ActivatableBody, Body2DLike } from './body2D.ts';

/** Plain snapshot of a body's motion state (a value; holds copies, not live engine vectors).
 * Mirrors the C# record struct `BodyState2D` (where `isActive` is named `IsEnabled`). Exported from
 * the physics index as the type `BodyState2DValue`. Use with {@link capture} / {@link restore} for
 * rollback, replay or prediction. */
export interface BodyState2D {
  /** Body origin in world units. */
  position: Vec2Like;
  /** Rotation in radians, counter-clockwise. */
  angle: number;
  /** Linear velocity in units per second. */
  linearVelocity: Vec2Like;
  /** Angular velocity in radians per second, counter-clockwise. */
  angularVelocity: number;
  /** Whether the body was awake. */
  isAwake: boolean;
  /** Whether the body took part in the simulation (C# `IsEnabled`); `true` for bodies without
   * `isActive`. */
  isActive: boolean;
}

/** Reads position, angle, velocities and the awake / active flags into a new {@link BodyState2D}
 * (vectors copied). A body without `isActive` counts as active. Mirrors C# `BodyState2D.Capture`
 * (and `BodyMotionExtensions2D.CaptureState`). */
export function capture(body: Body2DLike & Partial<ActivatableBody>): BodyState2D {
  const p = body.getPosition();
  const v = body.getLinearVelocity();
  return {
    position: { x: p.x, y: p.y },
    angle: body.getAngle(),
    linearVelocity: { x: v.x, y: v.y },
    angularVelocity: body.getAngularVelocity(),
    isAwake: body.isAwake(),
    isActive: body.isActive ? body.isActive() : true,
  };
}

/** Writes, in order: active (only with `restoreActive`), transform, linear and angular velocity,
 * awake (`true` when `wake`, else the captured value). Mirrors C# `BodyState2D.Restore` (whose flag is
 * named `restoreEnabled`). To copy a live body's motion without a snapshot use
 * {@link BodyMotion2D.copyMotionFrom}.
 * @param state - Snapshot from {@link capture}.
 * @param body - Body to write.
 * @param wake - True (default) forces the body awake; false restores the captured flag.
 * @param restoreActive - Also restore the active flag, first (default false; ignored when the body
 *   has no `setActive`). */
export function restore(state: BodyState2D, body: Body2DLike & Partial<ActivatableBody>, wake = true, restoreActive = false): void {
  if (restoreActive && body.setActive) body.setActive(state.isActive);
  body.setTransform({ x: state.position.x, y: state.position.y }, state.angle);
  body.setLinearVelocity({ x: state.linearVelocity.x, y: state.linearVelocity.y });
  body.setAngularVelocity(state.angularVelocity);
  body.setAwake(wake || state.isAwake);
}
