/**
 * Physics layer. 1D motion under constant acceleration — mirror of C# `Altruist.Physx.Kinematics`.
 */

/** Distance needed to stop from `speed` at constant `deceleration` (> 0): `speed * speed / (2 * deceleration)`.
 * Scalar, 1D. Mirrors C# `Kinematics.StoppingDistance`. For a body's speed along a direction use
 * {@link GameplayVerbs2D.brakingDistanceAlong}. */
export function stoppingDistance(speed: number, deceleration: number): number {
  return (speed * speed) / (2 * deceleration);
}

/** Seconds to cover `distance` starting at `initialSpeed`, accelerating at `acceleration` up to
 * `maxSpeed`, then cruising: if the distance is covered while accelerating, solves
 * `(-v0 + sqrt(v0² + 2·a·d)) / a`; otherwise `tAcc + (distance - dAcc) / maxSpeed`. An
 * `initialSpeed` above `maxSpeed` counts as no acceleration phase. Mirrors C# `Kinematics.TimeToCover`. */
export function timeToCover(distance: number, initialSpeed: number, acceleration: number, maxSpeed: number): number {
  const v0 = initialSpeed;
  const tAcc = Math.max(0, (maxSpeed - v0) / acceleration);
  const dAcc = ((v0 + maxSpeed) / 2) * tAcc;
  if (dAcc >= distance) return (-v0 + Math.sqrt(v0 * v0 + 2 * acceleration * distance)) / acceleration;
  return tAcc + (distance - dAcc) / maxSpeed;
}

/** Centripetal acceleration for a scalar speed on a curve of `radius`: `speed * speed / radius`.
 * Mirrors C# `Kinematics.CentripetalAcceleration`; the vector form is
 * {@link Velocity2D.centripetalAcceleration}. */
export function centripetalAcceleration(speed: number, radius: number): number {
  return (speed * speed) / radius;
}
