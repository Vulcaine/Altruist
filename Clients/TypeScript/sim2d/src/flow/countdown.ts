/**
 * Flow layer. Countdown timers — mirror of C# `Countdown` and `TimerSet`. `tick` is
 * `Math.max(0, t - dt)`, the inline expression. TypeScript has no `ref`, so the field forms take an
 * object and a key (C#: `Countdown.Tick(ref v.Cooldown, dt)` ⇔ `Countdown.tickField(v, 'cooldown', dt)`).
 */

/** True while the timer has time left (`t > 0`). */
export function isRunning(t: number): boolean {
  return t > 0;
}

/** `Math.max(0, t - dt)`. */
export function tick(t: number, dt: number): number {
  return Math.max(0, t - dt);
}

/** `Math.min(t + dt, cap)` (a capped count-up clock). */
export function countUp(t: number, dt: number, cap: number): number {
  return Math.min(t + dt, cap);
}

type NumberKeys<T> = { [K in keyof T]: T[K] extends number ? K : never }[keyof T];

/** `obj[key] = Math.max(0, obj[key] - dt)`; true when it ran out in this tick (was above 0, is 0). */
export function tickField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number): boolean {
  const before = obj[key] as unknown as number;
  const after = Math.max(0, before - dt);
  (obj[key] as unknown as number) = after;
  return before > 0 && after === 0;
}

/** Ticks only a running timer (`> 0`); true when it ran out in this tick. */
export function tickRunningField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number): boolean {
  const before = obj[key] as unknown as number;
  if (before <= 0) return false;
  const after = Math.max(0, before - dt);
  (obj[key] as unknown as number) = after;
  return after === 0;
}

/** `obj[key] = Math.min(obj[key] + dt, cap)`. */
export function countUpField<T, K extends NumberKeys<T>>(obj: T, key: K, dt: number, cap: number): void {
  (obj[key] as unknown as number) = Math.min((obj[key] as unknown as number) + dt, cap);
}
