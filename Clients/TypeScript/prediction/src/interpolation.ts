/**
 * Remote-frame interpolation: draw objects you do not predict (other players in an interpolated
 * client, spectators, replays) a little in the past, between two received states.
 */

/** Linear interpolation `a + (b - a) * t` (unclamped). */
export function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

/** Angle interpolation along the shortest arc, radians: `a + wrap(b - a) * t` with the difference
 * wrapped to [-π, π]. */
export function lerpAngle(a: number, b: number, t: number): number {
  const d = b - a;
  return a + (d - 2 * Math.PI * Math.round(d / (2 * Math.PI))) * t;
}

/** Tuning of an {@link InterpolationBuffer}. */
export interface InterpolationOptions {
  /** States kept (oldest dropped first). Default 32. */
  capacity?: number;
  /** How far past the newest state `sample` may extrapolate, as a fraction of the last gap (0: hold
   * the newest state). Default 0. */
  maxExtrapolation?: number;
}

/**
 * Timestamped states of one remote source (a snapshot stream, one entity), sampled at a render time
 * between two of them with a game-supplied lerp. Time is any monotonic unit (server ticks, seconds)
 * as long as `push` and `sample` agree. Typical use: push each snapshot at its tick, sample at
 * `newestTick - delayTicks` where the delay covers the snapshot interval plus jitter (2-3 snapshot
 * intervals).
 *
 * Use it for what you do not predict; for the predicted local world use {@link PredictedSession}
 * (which interpolates between its own last two steps). States pushed out of order are inserted in
 * order; a state with the time of an existing one replaces it. Deterministic.
 * @example
 * ```ts
 * const buf = new InterpolationBuffer<Ship>();
 * buf.push(snap.tick, snap.ship);
 * const drawn = buf.sample(buf.newestTime! - 2.5, (a, b, t) => ({ x: lerp(a.x, b.x, t), a: lerpAngle(a.a, b.a, t) }));
 * ```
 */
export class InterpolationBuffer<T> {
  private readonly items: { time: number; value: T }[] = [];
  private readonly capacity: number;
  private readonly maxExtrapolation: number;

  constructor(options: InterpolationOptions = {}) {
    this.capacity = Math.max(2, options.capacity ?? 32);
    this.maxExtrapolation = Math.max(0, options.maxExtrapolation ?? 0);
  }

  /** Adds the state at `time`. */
  push(time: number, value: T): void {
    let i = this.items.length;
    while (i > 0 && this.items[i - 1]!.time > time) i--;
    if (i > 0 && this.items[i - 1]!.time === time) this.items[i - 1] = { time, value };
    else this.items.splice(i, 0, { time, value });
    while (this.items.length > this.capacity) this.items.shift();
  }

  /** Time of the newest state (undefined when empty). */
  get newestTime(): number | undefined {
    return this.items[this.items.length - 1]?.time;
  }

  /** States held. */
  get size(): number {
    return this.items.length;
  }

  /**
   * The state at `time`: `lerp(a, b, t)` between the two states around it; before the oldest the
   * oldest; after the newest the newest (or extrapolated up to `maxExtrapolation`). Undefined when
   * empty.
   */
  sample(time: number, lerpFn: (a: T, b: T, t: number) => T): T | undefined {
    const n = this.items.length;
    if (n === 0) return undefined;
    const first = this.items[0]!;
    if (n === 1 || time <= first.time) return first.value;
    const last = this.items[n - 1]!;
    if (time >= last.time) {
      if (this.maxExtrapolation === 0) return last.value;
      const prev = this.items[n - 2]!;
      const gap = last.time - prev.time;
      const t = 1 + Math.min((time - last.time) / gap, this.maxExtrapolation);
      return lerpFn(prev.value, last.value, t);
    }
    let i = n - 1;
    while (this.items[i - 1]!.time > time) i--;
    const a = this.items[i - 1]!;
    const b = this.items[i]!;
    return lerpFn(a.value, b.value, (time - a.time) / (b.time - a.time));
  }

  /** Drops every state. */
  clear(): void {
    this.items.length = 0;
  }
}
