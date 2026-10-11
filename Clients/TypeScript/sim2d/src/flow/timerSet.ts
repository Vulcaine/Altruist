/** Flow layer. An indexed set of countdown timers — mirror of C# `TimerSet`. */
/** A fixed set of countdown timers ticked together in index order with `Math.max(0, t - dt)`, with
 * expiry edges per tick. Mirrors C# `TimerSet`. For a single timer field use
 * {@link Countdown.tickField}.
 * @example
 * ```ts
 * const timers = new TimerSet(['stun', 'boost']);
 * const STUN = timers.indexOf('stun');
 * timers.start(STUN, 0.5);
 * timers.tick(dt);
 * if (timers.expiredAt(STUN)) onStunEnd();
 * ```
 */
export class TimerSet {
  /** The remaining times in seconds (index order), writable (e.g. to restore a snapshot). C#
   * `TimerSet.Remaining` / indexer. */
  readonly remaining: number[];
  private readonly expired: boolean[];
  private readonly names: readonly string[] | null;

  /** `count` timers, or one per name (names enable {@link indexOf}). All start at 0 (not running).
   * Mirrors both C# `TimerSet` constructors. */
  constructor(countOrNames: number | readonly string[]) {
    const count = typeof countOrNames === 'number' ? countOrNames : countOrNames.length;
    this.remaining = new Array<number>(count).fill(0);
    this.expired = new Array<boolean>(count).fill(false);
    this.names = typeof countOrNames === 'number' ? null : [...countOrNames];
  }

  /** Number of timers. C# `TimerSet.Count`. */
  get count(): number {
    return this.remaining.length;
  }

  /** Index of the timer named `name`; throws when there is none (or the set was made from a count).
   * Look it up once at setup. C# `TimerSet.IndexOf`. */
  indexOf(name: string): number {
    const i = this.names ? this.names.indexOf(name) : -1;
    if (i < 0) throw new Error(`No timer named '${name}'.`);
    return i;
  }

  /** Sets timer `index` to `seconds` (restarts a running one). C# `TimerSet.Start`. */
  start(index: number, seconds: number): void {
    this.remaining[index] = seconds;
  }

  /** True while timer `index` has time left. C# `TimerSet.IsRunning`. */
  isRunning(index: number): boolean {
    return this.remaining[index]! > 0;
  }

  /** True when the timer ran out in the last {@link tick}. C# `TimerSet.Expired`. */
  expiredAt(index: number): boolean {
    return this.expired[index]!;
  }

  /** `t = Math.max(0, t - dt)` for every timer, index order, recording expiry edges. C# `TimerSet.Tick`. */
  tick(dt: number): void {
    const r = this.remaining;
    for (let i = 0; i < r.length; i++) {
      const running = r[i]! > 0;
      r[i] = Math.max(0, r[i]! - dt);
      this.expired[i] = running && r[i] === 0;
    }
  }

  /** Stops every timer and clears the expiry edges. C# `TimerSet.Reset`. */
  reset(): void {
    this.remaining.fill(0);
    this.expired.fill(false);
  }
}
