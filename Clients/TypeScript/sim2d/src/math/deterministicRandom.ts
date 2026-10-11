/**
 * Math layer. Seedable xorshift32 — mirror of C# `Altruist.Numerics.DeterministicRandom`, the SAME
 * sequence for the same seed in both languages (uint32 arithmetic via `Math.imul` and `>>> 0`).
 * Not cryptographically secure.
 */
export class DeterministicRandom {
  private s: number;

  /**
   * Seeds the generator: `(imul(seed | 0, 0x9e3779b1) ^ 0x2545f491) >>> 0`, or `0x1234567` if that
   * is 0 (xorshift must never hold 0). Mirrors the C# `DeterministicRandom(int seed)` constructor.
   * @param seed - Any number; truncated to int32 first.
   */
  constructor(seed: number) {
    this.s = (Math.imul(seed | 0, 0x9e3779b1) ^ 0x2545f491) >>> 0 || 0x1234567;
  }

  /** A generator continuing from a saved {@link DeterministicRandom.state} (use to snapshot /
   * restore / replay). Mirrors C# `DeterministicRandom.FromState`. */
  static fromState(state: number): DeterministicRandom {
    const r = new DeterministicRandom(0);
    r.state = state;
    return r;
  }

  /** Raw uint32 state; setting 0 stores 0x1234567. Mirrors C# `DeterministicRandom.State`. */
  get state(): number {
    return this.s;
  }

  set state(value: number) {
    this.s = value >>> 0 || 0x1234567;
  }

  /** Advances xorshift32 (13, 17, 5) and returns the new raw uint32 state (never 0). Mirrors C#
   * `DeterministicRandom.NextUInt`. */
  nextUint(): number {
    let s = this.s;
    s ^= s << 13;
    s >>>= 0;
    s ^= s >>> 17;
    s ^= s << 5;
    s >>>= 0;
    this.s = s;
    return s;
  }

  /** Uniform in [0, 1): `nextUint() / 4294967296` (double in both languages). Mirrors C#
   * `DeterministicRandom.Next`. */
  next(): number {
    return this.nextUint() / 4294967296;
  }

  /** Uniform in [-1, 1): `next() * 2 - 1`. Mirrors C# `DeterministicRandom.Signed`, which rounds the
   * result to float32 (so the TS value can differ below float precision). */
  signed(): number {
    return this.next() * 2 - 1;
  }

  /** Approximately normal, mean 0, range [-3, 3) (sum of three uniforms; draws three times):
   * `(next() + next() + next() - 1.5) * 2`. Mirrors C# `DeterministicRandom.Gaussian` (float32 there). */
  gaussian(): number {
    return (this.next() + this.next() + this.next() - 1.5) * 2;
  }

  /** Uniform integer in [-n, n] (0 without drawing when n ≤ 0). Mirrors C# `DeterministicRandom.Jitter`. */
  jitter(n: number): number {
    return n <= 0 ? 0 : Math.floor(this.next() * (2 * n + 1)) - n;
  }

  /** True with probability `p`: `next() < p` (always draws, even for p ≤ 0 or ≥ 1, so the sequence
   * stays aligned). Mirrors C# `DeterministicRandom.Chance`. */
  chance(p: number): boolean {
    return this.next() < p;
  }
}
