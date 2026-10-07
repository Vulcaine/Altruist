/**
 * Math layer. Seedable xorshift32 — mirror of C# `Altruist.Numerics.DeterministicRandom`, the SAME
 * sequence for the same seed in both languages (uint32 arithmetic via `Math.imul` and `>>> 0`).
 * Not cryptographically secure.
 */
export class DeterministicRandom {
  private s: number;

  constructor(seed: number) {
    this.s = (Math.imul(seed | 0, 0x9e3779b1) ^ 0x2545f491) >>> 0 || 0x1234567;
  }

  /** A generator continuing from a saved `state`. */
  static fromState(state: number): DeterministicRandom {
    const r = new DeterministicRandom(0);
    r.state = state;
    return r;
  }

  /** Raw uint32 state; setting 0 stores 0x1234567. */
  get state(): number {
    return this.s;
  }

  set state(value: number) {
    this.s = value >>> 0 || 0x1234567;
  }

  /** Next raw uint32 (never 0). */
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

  /** Uniform in [0, 1): `nextUint() / 4294967296`. */
  next(): number {
    return this.nextUint() / 4294967296;
  }

  /** Uniform in [-1, 1): `next() * 2 - 1` (C# rounds the result to float32). */
  signed(): number {
    return this.next() * 2 - 1;
  }

  /** Approximately normal (sum of three uniforms): `(next() + next() + next() - 1.5) * 2`. */
  gaussian(): number {
    return (this.next() + this.next() + this.next() - 1.5) * 2;
  }

  /** Integer in [-n, n] (0 without drawing when n ≤ 0). */
  jitter(n: number): number {
    return n <= 0 ? 0 : Math.floor(this.next() * (2 * n + 1)) - n;
  }

  /** `next() < p` (always draws). */
  chance(p: number): boolean {
    return this.next() < p;
  }
}
