/**
 * Physics layer. 1D motion under constant acceleration — mirror of C# `Altruist.Physx.Kinematics`.
 */

/** `speed * speed / (2 * deceleration)`. */
export function stoppingDistance(speed: number, deceleration: number): number {
  return (speed * speed) / (2 * deceleration);
}

/** Time to cover `distance` from `initialSpeed`, accelerating up to `maxSpeed`, then cruising. */
export function timeToCover(distance: number, initialSpeed: number, acceleration: number, maxSpeed: number): number {
  const v0 = initialSpeed;
  const tAcc = Math.max(0, (maxSpeed - v0) / acceleration);
  const dAcc = ((v0 + maxSpeed) / 2) * tAcc;
  if (dAcc >= distance) return (-v0 + Math.sqrt(v0 * v0 + 2 * acceleration * distance)) / acceleration;
  return tAcc + (distance - dAcc) / maxSpeed;
}
