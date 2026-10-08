/** Flow layer. Stick deadzone queries — mirror of C# `Stick` (the same comparisons). */
import { lengthXY } from '../math/vectorMath2D.ts';

/** `Math.abs(axis) > deadzone`. */
export function axisBeyond(axis: number, deadzone: number): boolean {
  return Math.abs(axis) > deadzone;
}

/** `lengthXY(x, y) < deadzone`. */
export function insideDeadzone(x: number, y: number, deadzone: number): boolean {
  return lengthXY(x, y) < deadzone;
}

/** `lengthXY(x, y) >= deadzone`. */
export function beyond(x: number, y: number, deadzone: number): boolean {
  return lengthXY(x, y) >= deadzone;
}
