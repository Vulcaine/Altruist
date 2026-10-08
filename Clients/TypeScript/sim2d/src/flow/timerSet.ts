/** Flow layer. An indexed set of countdown timers — mirror of C# `TimerSet`. */
/** Timers ticked in index order with `Math.max(0, t - dt)`; expiry edges per tick. */
export class TimerSet {
  /** The remaining times (index order), writable. */
  readonly remaining: number[];
  private readonly expired: boolean[];
  private readonly names: readonly string[] | null;

  /** `count` timers, or one per name. */
  constructor(countOrNames: number | readonly string[]) {
    const count = typeof countOrNames === 'number' ? countOrNames : countOrNames.length;
    this.remaining = new Array<number>(count).fill(0);
    this.expired = new Array<boolean>(count).fill(false);
    this.names = typeof countOrNames === 'number' ? null : [...countOrNames];
  }

  get count(): number {
    return this.remaining.length;
  }

  indexOf(name: string): number {
    const i = this.names ? this.names.indexOf(name) : -1;
    if (i < 0) throw new Error(`No timer named '${name}'.`);
    return i;
  }

  start(index: number, seconds: number): void {
    this.remaining[index] = seconds;
  }

  isRunning(index: number): boolean {
    return this.remaining[index]! > 0;
  }

  /** True when the timer ran out in the last `tick`. */
  expiredAt(index: number): boolean {
    return this.expired[index]!;
  }

  /** `t = Math.max(0, t - dt)` for every timer, index order. */
  tick(dt: number): void {
    const r = this.remaining;
    for (let i = 0; i < r.length; i++) {
      const running = r[i]! > 0;
      r[i] = Math.max(0, r[i]! - dt);
      this.expired[i] = running && r[i] === 0;
    }
  }

  reset(): void {
    this.remaining.fill(0);
    this.expired.fill(false);
  }
}
