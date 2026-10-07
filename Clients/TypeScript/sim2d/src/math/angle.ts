/**
 * Math layer. Angle bookkeeping in radians — mirror of C# `Altruist.Numerics.Angle`.
 * Same expressions, same order, on JavaScript numbers.
 */
import { clamp } from './scalar.ts';

const TWO_PI = Math.PI * 2;

/** Wrap into [-π, π] with an IEEE remainder (C# `Angle.Normalize`). */
export function normalize(radians: number): number {
  // IEEERemainder(x, y) = x - y * roundHalfEven(x / y)
  const q = radians / TWO_PI;
  let n = Math.round(q);
  if (Math.abs(q % 1) === 0.5 && n % 2 !== 0) n -= 1;
  let r = radians - TWO_PI * n;
  if (r > Math.PI) r -= TWO_PI;
  else if (r < -Math.PI) r += TWO_PI;
  return r;
}

/** `normalize(to - from)`. */
export function shortestDifference(from: number, to: number): number {
  return normalize(to - from);
}

/** Steps `current` toward `target` by at most `maxDelta`, the short way around. */
export function moveToward(current: number, target: number, maxDelta: number): number {
  if (maxDelta <= 0) return current;
  const diff = shortestDifference(current, target);
  if (Math.abs(diff) <= maxDelta) return target;
  return normalize(current + Math.sign(diff) * maxDelta);
}

/** `degrees * (Math.PI / 180)`. */
export function toRadians(degrees: number): number {
  return degrees * (Math.PI / 180);
}

/** `radians * (180 / Math.PI)`. */
export function toDegrees(radians: number): number {
  return radians * (180 / Math.PI);
}

/** Modulo wrap: `a = (radians + π) % 2π; if (a < 0) a += 2π; return a - π` — result in [-π, π),
 * +π maps to -π. */
export function wrap(radians: number): number {
  let a = (radians + Math.PI) % TWO_PI;
  if (a < 0) a += TWO_PI;
  return a - Math.PI;
}

/** `wrap(to - from)`. */
export function wrappedDelta(from: number, to: number): number {
  return wrap(to - from);
}

/** The angle equivalent to `angle` nearest to `reference`: `reference + wrap(angle - reference)`. */
export function nearestEquivalent(reference: number, angle: number): number {
  return reference + wrap(angle - reference);
}

/** The multiple of 2π nearest to `radians`: `radians - wrap(radians)`. */
export function nearestFullTurn(radians: number): number {
  return radians - wrap(radians);
}

/** `wrap(target - current) * gain`. */
export function rateToward(current: number, target: number, gain: number): number {
  return wrap(target - current) * gain;
}

/** `clamp(wrap(target - current) * gain, -maxRate, maxRate)`. */
export function clampedRateToward(current: number, target: number, gain: number, maxRate: number): number {
  return clamp(wrap(target - current) * gain, -maxRate, maxRate);
}

/** `wrap(target - current) / Math.max(time, minTime)`. */
export function arriveRate(current: number, target: number, time: number, minTime: number): number {
  return wrap(target - current) / Math.max(time, minTime);
}
