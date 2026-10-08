/**
 * Flow layer. "Entered this step" detection — mirror of C# `OverlapLatch32` and `OverlapLatch<TId>`.
 */
import { bit } from './idMask32.ts';

/**
 * Overlaps against small integer ids as bit masks. Each step: `begin()`, then `record(id)` for every
 * overlap (all of them, even after the one acted on); `record` returns true only for a fresh entry.
 * Store only `current` with the entity.
 */
export class OverlapLatch32 {
  /** Overlaps recorded in the step before the current one. */
  previous = 0;
  /** Overlaps recorded so far in the current step (the value to store). */
  current: number;

  constructor(current = 0) {
    this.current = current;
  }

  /** Starts a step: the current overlaps become the previous ones. */
  begin(): void {
    this.previous = this.current;
    this.current = 0;
  }

  /** Records an overlap; true when it is fresh. */
  record(id: number): boolean {
    const b = bit(id);
    this.current |= b;
    return (this.previous & b) === 0;
  }

  wasOverlapping(id: number): boolean {
    return (this.previous & bit(id)) !== 0;
  }

  isOverlapping(id: number): boolean {
    return (this.current & bit(id)) !== 0;
  }
}

/** The general form for any id type (Set-based); fresh entries are also listed in record order. */
export class OverlapLatch<TId> {
  private prev = new Set<TId>();
  private cur = new Set<TId>();
  private readonly enteredIds: TId[] = [];

  get entered(): readonly TId[] {
    return this.enteredIds;
  }

  get current(): ReadonlySet<TId> {
    return this.cur;
  }

  get previous(): ReadonlySet<TId> {
    return this.prev;
  }

  begin(): void {
    const t = this.prev;
    this.prev = this.cur;
    this.cur = t;
    this.cur.clear();
    this.enteredIds.length = 0;
  }

  record(id: TId): boolean {
    if (this.cur.has(id)) return false;
    this.cur.add(id);
    if (this.prev.has(id)) return false;
    this.enteredIds.push(id);
    return true;
  }

  wasOverlapping(id: TId): boolean {
    return this.prev.has(id);
  }

  reset(): void {
    this.prev.clear();
    this.cur.clear();
    this.enteredIds.length = 0;
  }
}
