/** Flow layer. Stick deadzone queries — mirror of C# `Stick` (the same comparisons). */
import { lengthXY } from '../math/vectorMath2D.ts';

/** True when one axis is outside the deadzone: `Math.abs(axis) > deadzone`. Mirrors C# `Stick.AxisBeyond`. */
export function axisBeyond(axis: number, deadzone: number): boolean {
  return Math.abs(axis) > deadzone;
}

/** True when the stick is inside the radial deadzone: `lengthXY(x, y) < deadzone` (`Math.sqrt`, not
 * `Math.hypot`). Exact complement of {@link beyond}. Mirrors C# `Stick.InsideDeadzone`. */
export function insideDeadzone(x: number, y: number, deadzone: number): boolean {
  return lengthXY(x, y) < deadzone;
}

/** True when the stick is at or beyond the radial deadzone: `lengthXY(x, y) >= deadzone`. Mirrors C#
 * `Stick.Beyond`. */
export function beyond(x: number, y: number, deadzone: number): boolean {
  return lengthXY(x, y) >= deadzone;
}
