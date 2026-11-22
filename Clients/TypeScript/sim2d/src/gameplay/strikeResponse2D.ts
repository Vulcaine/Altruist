/**
 * Gameplay layer. How a round body (a ball, a puck) leaves when another body strikes it — mirror of
 * C# `Altruist.Gaming.TwoD.StrikeResponse2D`. A designed arcade response instead of the solver's,
 * configured once per body kind and applied with {@link strike} or {@link launch} from a
 * {@link ContactImpact2D} captured before the solver; game code only scales the {@link rebound}.
 * The target leaves along the contact normal; along the surface it blends its own motion with the
 * striker's (`surfaceCarry`) and spins up from the slip between them. Same operation order as C#.
 * @example
 * ```ts
 * const hit = ContactImpact2D.capture(point, normal, striker, ball);
 * StrikeResponse2D.strike(response, ball, hit, StrikeResponse2D.rebound(response, hit) * multiplier, radius);
 * ```
 */
import type { AngularBody, LinearBody } from '../physics/body2D.ts';
import { addSpinFromSlip, setVelocityInFrame } from '../physics/bodyMotion2D.ts';
import type { ContactImpact2D } from '../physics/contactImpact2D.ts';

/** The response's settings. C# `StrikeResponse2D`'s properties. */
export interface StrikeResponseSettings2D {
  /** How much of the closing speed bounces back on top of it. */
  readonly restitution: number;
  /** Share of the target's own speed it keeps on top of the strike. */
  readonly speedCarry: number;
  /** Share of the striker's motion along the contact surface the target takes. */
  readonly surfaceCarry: number;
  /** Spin from the slip between the surfaces, per unit of `slip / radius`. */
  readonly spinFactor: number;
}

/** The strike's base speed along the normal: `hit.closingSpeed * (1 + restitution)`. Mirrors C# `StrikeResponse2D.Rebound`. */
export function rebound(response: StrikeResponseSettings2D, hit: ContactImpact2D): number {
  return hit.closingSpeed * (1 + response.restitution);
}

/** The target leaves along the normal at `power` plus `speedCarry` of its own speed:
 * `launch(response, target, hit, power + hit.targetSpeed * speedCarry, radius)`. Mirrors C# `StrikeResponse2D.Strike`. */
export function strike(response: StrikeResponseSettings2D, target: LinearBody & AngularBody, hit: ContactImpact2D, power: number, radius: number): void {
  launch(response, target, hit, power + hit.targetSpeed * response.speedCarry, radius);
}

/** The target leaves along the normal at exactly `alongNormal`: velocity
 * `hit.frame.compose(alongNormal, hit.targetTangential * (1 - surfaceCarry) + hit.strikerTangential * surfaceCarry)`,
 * then spin from `hit.slip`. Mirrors C# `StrikeResponse2D.Launch`. */
export function launch(response: StrikeResponseSettings2D, target: LinearBody & AngularBody, hit: ContactImpact2D, alongNormal: number, radius: number): void {
  const alongTangent = hit.targetTangential * (1 - response.surfaceCarry) + hit.strikerTangential * response.surfaceCarry;
  setVelocityInFrame(target, hit.frame, alongNormal, alongTangent);
  addSpinFromSlip(target, hit.slip, radius, response.spinFactor);
}
