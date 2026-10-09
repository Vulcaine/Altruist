/**
 * Flow layer. Countdown timers — mirror of C# `Countdown` and `TimerSet`. `tick` is
 * `Math.max(0, t - dt)`, the inline expression. TypeScript has no `ref`, so the field forms take an
 * object and a key (C#: `Countdown.Tick(ref v.Cooldown, dt)` ⇔ `Countdown.tickField(v, 'cooldown', dt)`).
 */

/** True while the timer has time left (`t > 0`). Mirrors C# `Countdown.IsRunning`. */
export function isRunning(t: number): boolean {
  return t > 0;
}

/** Value form of one countdown step: returns `Math.max(0, t - dt)` (`dt` in seconds). To also learn
 * whether it ran out this tick use {@link tickField}; for many timers see {@link TimerSet}. The C#
 * `Countdown.Tick(ref t, dt)` writes back and returns that edge instead. */
export function tick(t: number, dt: number): number {
  return Math.max(0, t - dt);
}

/** Value form of a capped count-up clock (time since an event): returns `Math.min(t + dt, cap)`. The
 * C# `Countdown.CountUp(ref t, dt, cap)` writes back. */
export function countUp(t: number, dt: number, cap: number): number {
  return Math.min(t + dt, cap);
}

type NumberKeys<T> = { [K in keyof T]: T[K] extends number ? K : never }[keyof T];

/** `obj[key] = Math.max(0, obj[key] - dt)`; true when it ran out in this tick (was above 0, is 0).
 * Mutates `obj`. Mirrors C# `Countdown.Tick(ref obj.Key, dt)`.
 * @example
 * ```ts
 * if (Countdown.tickField(unit, 'cooldown', dt)) unit.ready = true;
 * ```
 */
export function tickField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number): boolean {
  const before = obj[key] as unknown as number;
  const after = Math.max(0, before - dt);
  (obj[key] as unknown as number) = after;
  return before > 0 && after === 0;
}

/** Ticks only a running timer (`> 0`; one at or below 0 is left untouched, not clamped); true when it
 * ran out in this tick. Mutates `obj`. Mirrors C# `Countdown.TickRunning`. */
export function tickRunningField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number): boolean {
  const before = obj[key] as unknown as number;
  if (before <= 0) return false;
  const after = Math.max(0, before - dt);
  (obj[key] as unknown as number) = after;
  return after === 0;
}

/** `obj[key] = Math.min(obj[key] + dt, cap)` (mutates `obj`). Mirrors C# `Countdown.CountUp`. */
export function countUpField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number, cap: number): void {
  (obj[key] as unknown as number) = Math.min((obj[key] as unknown as number) + dt, cap);
}
