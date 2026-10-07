/**
 * Physics layer. Body motion snapshot — mirror of C# `Altruist.Physx.TwoD.BodyState2D`.
 */
import type { Vec2Like } from '../math/vec2.ts';
import type { ActivatableBody, Body2DLike } from './body2D.ts';

export interface BodyState2D {
  position: Vec2Like;
  angle: number;
  linearVelocity: Vec2Like;
  angularVelocity: number;
  isAwake: boolean;
  isActive: boolean;
}

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
 * awake (`true` when `wake`, else the captured value). */
export function restore(state: BodyState2D, body: Body2DLike & Partial<ActivatableBody>, wake = true, restoreActive = false): void {
  if (restoreActive && body.setActive) body.setActive(state.isActive);
  body.setTransform({ x: state.position.x, y: state.position.y }, state.angle);
  body.setLinearVelocity({ x: state.linearVelocity.x, y: state.linearVelocity.y });
  body.setAngularVelocity(state.angularVelocity);
  body.setAwake(wake || state.isAwake);
}
