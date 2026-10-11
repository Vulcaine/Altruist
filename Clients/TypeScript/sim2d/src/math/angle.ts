/**
 * Math layer. Angle bookkeeping in radians — mirror of C# `Altruist.Numerics.Angle`.
 * Same expressions, same order, on JavaScript numbers.
 */
import { clamp } from './scalar.ts';

const TWO_PI = Math.PI * 2;

/**
 * Wraps an angle into [-π, π] with an IEEE remainder (round-half-even quotient). Mirrors C#
 * `Angle.Normalize` (`MathF.IEEERemainder`).
 *
 * Differs from {@link wrap} at the boundary: `normalize(π)` stays `+π` while `wrap(π)` gives `-π`,
 * and other inputs may differ in the last bit. Use `normalize` for general angle bookkeeping; use
 * {@link wrap} when the result must match `(a + π) % 2π` code bit for bit.
 * @param radians - Any angle in radians (counter-clockwise positive).
 * @returns The equivalent angle in [-π, π].
 */
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

/** Signed shortest turn from `from` to `to` (radians, positive = counter-clockwise):
 * `normalize(to - from)`, in [-π, π]. Mirrors C# `Angle.ShortestDifference`. For the
 * modulo-wrapped variant (result in [-π, π)) use {@link wrappedDelta}. */
export function shortestDifference(from: number, to: number): number {
  return normalize(to - from);
}

/** Steps `current` toward `target` by at most `maxDelta` radians, the short way around; never
 * overshoots (returns `target` exactly when within reach, `current` when `maxDelta <= 0`). The
 * stepped result is {@link normalize}d into [-π, π]. Mirrors C# `Angle.MoveToward`. Use it for
 * turn-rate-limited headings; for a per-second rate instead of a position see {@link rateToward}. */
export function moveToward(current: number, target: number, maxDelta: number): number {
  if (maxDelta <= 0) return current;
  const diff = shortestDifference(current, target);
  if (Math.abs(diff) <= maxDelta) return target;
  return normalize(current + Math.sign(diff) * maxDelta);
}

/** Degrees to radians: `degrees * (Math.PI / 180)`. Mirrors C# `Angle.ToRadians`. */
export function toRadians(degrees: number): number {
  return degrees * (Math.PI / 180);
}

/** Radians to degrees: `radians * (180 / Math.PI)`. Mirrors C# `Angle.ToDegrees`. */
export function toDegrees(radians: number): number {
  return radians * (180 / Math.PI);
}

/** Modulo wrap: `a = (radians + π) % 2π; if (a < 0) a += 2π; return a - π` — result in [-π, π),
 * +π maps to -π. Mirrors C# `Angle.Wrap` bit for bit in expression order.
 *
 * Use this when the result must match `(a + π) % 2π`-style code exactly; otherwise prefer
 * {@link normalize} (IEEE remainder, range [-π, π], keeps +π). All the `*Toward` / rate helpers
 * below are built on `wrap`. */
export function wrap(radians: number): number {
  let a = (radians + Math.PI) % TWO_PI;
  if (a < 0) a += TWO_PI;
  return a - Math.PI;
}

/** Signed delta `wrap(to - from)`, in [-π, π). Mirrors C# `Angle.WrappedDelta`. The
 * IEEE-remainder variant (range [-π, π]) is {@link shortestDifference}. */
export function wrappedDelta(from: number, to: number): number {
  return wrap(to - from);
}

/** The angle equivalent to `angle` nearest to `reference`: `reference + wrap(angle - reference)`.
 * Use to unwrap an absolute angle next to an accumulated (multi-turn) one. Mirrors C#
 * `Angle.NearestEquivalent`. */
export function nearestEquivalent(reference: number, angle: number): number {
  return reference + wrap(angle - reference);
}

/** The multiple of 2π nearest to `radians`: `radians - wrap(radians)`. Mirrors C#
 * `Angle.NearestFullTurn`. */
export function nearestFullTurn(radians: number): number {
  return radians - wrap(radians);
}

/** Proportional angular rate (radians per second when `gain` is per second) that turns `current`
 * toward `target` the short way: `wrap(target - current) * gain`. Unclamped; see
 * {@link clampedRateToward}. Mirrors C# `Angle.RateToward`. */
export function rateToward(current: number, target: number, gain: number): number {
  return wrap(target - current) * gain;
}

/** {@link rateToward} limited to ±`maxRate`: `clamp(wrap(target - current) * gain, -maxRate, maxRate)`.
 * Mirrors C# `Angle.ClampedRateToward`. */
export function clampedRateToward(current: number, target: number, gain: number, maxRate: number): number {
  return clamp(wrap(target - current) * gain, -maxRate, maxRate);
}

/** Angular rate that closes the wrapped gap to `target` in `time` seconds (at least `minTime`, to
 * avoid dividing by ~0): `wrap(target - current) / Math.max(time, minTime)`. Mirrors C#
 * `Angle.ArriveRate`. */
export function arriveRate(current: number, target: number, time: number, minTime: number): number {
  return wrap(target - current) / Math.max(time, minTime);
}

/** Constant rate (radians per second) for one full turn in `time` seconds, signed by `direction`
 * (+1 counter-clockwise, -1 clockwise): `(direction * Math.PI * 2) / time`. Mirrors C#
 * `Angle.FullTurnRate` (where `direction` is an `int`). */
export function fullTurnRate(time: number, direction: number): number {
  return (direction * Math.PI * 2) / time;
}

/** Which of `sectors` equal slices the direction (dx, dy) is in, counter-clockwise from -x:
 * `Math.floor(((Math.atan2(dy, dx) + Math.PI) / (2 * Math.PI)) * sectors) % sectors` (+π wraps to 0).
 * Returns an integer in [0, sectors). Mirrors C# `Angle.SectorToward` (computed in double there too).
 * For an angle instead of a direction use {@link sector}. */
export function sectorToward(dx: number, dy: number, sectors: number): number {
  return Math.floor(((Math.atan2(dy, dx) + Math.PI) / (2 * Math.PI)) * sectors) % sectors;
}

/** `sectorToward(Math.cos(radians), Math.sin(radians), sectors)`, i.e.
 * `Math.floor(((Math.atan2(Math.sin(a), Math.cos(a)) + Math.PI) / (2 * Math.PI)) * sectors) % sectors`.
 * Mirrors C# `Angle.Sector`. */
export function sector(radians: number, sectors: number): number {
  return sectorToward(Math.cos(radians), Math.sin(radians), sectors);
}
