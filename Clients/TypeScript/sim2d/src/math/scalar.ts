/**
 * Math layer. "One number at a time" — mirror of C# `Altruist.Numerics.Scalar`. Every function
 * evaluates exactly the expression in its comment (same operands, same grouping) on JavaScript
 * numbers, so it can replace that expression written inline with identical results.
 */

/** Clamps `v` to [min, max]: `v < min ? min : v > max ? max : v`. Never throws; NaN stays NaN; when
 * min > max, `min` wins (see {@link clampMinMax}). Mirrors C# `Scalar.Clamp`. */
export function clamp(v: number, min: number, max: number): number {
  return v < min ? min : v > max ? max : v;
}

/** `Math.min(Math.max(v, min), max)`: unlike `clamp`, `max` wins when min > max and -0 becomes +0
 * at a bound of 0; NaN stays NaN. Use it only to reproduce existing `min(max())` code bit for bit;
 * otherwise prefer {@link clamp}. Mirrors C# `Scalar.ClampMinMax`. */
export function clampMinMax(v: number, min: number, max: number): number {
  return Math.min(Math.max(v, min), max);
}

/** `clamp(v, 0, 1)`. Mirrors C# `Scalar.Clamp01`. */
export function clamp01(v: number): number {
  return clamp(v, 0, 1);
}

/** Moves `current` toward `target` by at most `maxDelta` (≥ 0) without overshooting:
 * `current < target ? Math.min(current + maxDelta, target) : Math.max(current - maxDelta, target)`.
 * Linear (not angular); for angles use {@link Angle.moveToward}. Mirrors C# `Scalar.Approach`. */
export function approach(current: number, target: number, maxDelta: number): number {
  return current < target ? Math.min(current + maxDelta, target) : Math.max(current - maxDelta, target);
}

/** Linear interpolation `a + (b - a) * t` (unclamped). Mirrors C# `Scalar.Lerp`. */
export function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

/** Where `v` sits between `a` and `b`: `(v - a) / (b - a)` (unclamped; a === b divides by zero).
 * Mirrors C# `Scalar.InverseLerp`. */
export function inverseLerp(a: number, b: number, v: number): number {
  return (v - a) / (b - a);
}

/** Maps `v` from [inMin, inMax] to [outMin, outMax]: `lerp(outMin, outMax, inverseLerp(inMin, inMax, v))`
 * (unclamped). Mirrors C# `Scalar.Remap`. */
export function remap(v: number, inMin: number, inMax: number, outMin: number, outMax: number): number {
  return lerp(outMin, outMax, inverseLerp(inMin, inMax, v));
}

/** Easing curve on a clamped parameter: `Math.pow(clamp01(t), exponent)`. Mirrors C# `Scalar.Pow01`. */
export function pow01(t: number, exponent: number): number {
  return Math.pow(clamp01(t), exponent);
}

/** `v > 0 ? 1 : v < 0 ? -1 : 0` (NaN and ±0 give 0, unlike `Math.sign`, which returns NaN / -0).
 * Mirrors C# `Scalar.Sign`. */
export function sign(v: number): number {
  return v > 0 ? 1 : v < 0 ? -1 : 0;
}

/** {@link sign} with a chosen result for 0 / NaN: `v > 0 ? 1 : v < 0 ? -1 : fallback`. Mirrors C#
 * `Scalar.SignOr` (int result there). */
export function signOr(v: number, fallback = 0): number {
  return v > 0 ? 1 : v < 0 ? -1 : fallback;
}

/** Halves round up: `Math.round(v)` (JavaScript rounds ties toward +∞). The C# twin computes
 * `MathF.Floor(v + 0.5f)`, which agrees except right below 0.5 in float32. Mirrors C#
 * `Scalar.RoundHalfUp`. */
export function roundHalfUp(v: number): number {
  return Math.round(v);
}

/** `Math.round(clamp(v, -1, 1) * steps) / steps` (127 = a signed byte). C# rounds exact ties to
 * even, JavaScript up: the twins differ only for inputs exactly halfway between two steps. Use it to
 * make a value survive a network byte round-trip identically on both ends. Mirrors C#
 * `Scalar.Quantize`. */
export function quantize(v: number, steps = 127): number {
  return Math.round(clamp(v, -1, 1) * steps) / steps;
}
