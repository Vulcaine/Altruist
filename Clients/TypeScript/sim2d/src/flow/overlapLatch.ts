/**
 * Flow layer. "Entered this step" detection — mirror of C# `OverlapLatch32` and `OverlapLatch<TId>`.
 */
import { bit } from './idMask32.ts';

/**
 * Overlaps against small integer ids as bit masks. Each step: `begin()`, then `record(id)` for every
 * overlap (all of them, even after the one acted on); `record` returns true only for a fresh entry.
 * Store only `current` with the entity. Ids must be below 32 (they wrap). Mirrors the C# struct
 * `OverlapLatch32`; for arbitrary ids use {@link OverlapLatch}.
 * @example
 * ```ts
 * latch.begin();
 * for (const zone of zonesTouching(body)) if (latch.record(zone.id)) onEnter(zone);
 * ```
 */
export class OverlapLatch32 {
  /** Overlaps recorded in the step before the current one (bit mask). C# `OverlapLatch32.Previous`. */
  previous = 0;
  /** Overlaps recorded so far in the current step (bit mask; the value to store). C# `OverlapLatch32.Current`. */
  current: number;

  /** Restores a latch from a stored `current` mask (`previous` starts at 0). C# `new OverlapLatch32(current)`. */
  constructor(current = 0) {
    this.current = current;
  }

  /** Starts a step: the current overlaps become the previous ones. C# `OverlapLatch32.Begin`. */
  begin(): void {
    this.previous = this.current;
    this.current = 0;
  }

  /** Records an overlap; true when it is fresh (not overlapping in the previous step; recording twice
   * in a step reports fresh both times). C# `OverlapLatch32.Record`. */
  record(id: number): boolean {
    const b = bit(id);
    this.current |= b;
    return (this.previous & b) === 0;
  }

  /** True when `id` was overlapping in the previous step. C# `OverlapLatch32.WasOverlapping`. */
  wasOverlapping(id: number): boolean {
    return (this.previous & bit(id)) !== 0;
  }

  /** True when `id` has been recorded in the current step. C# `OverlapLatch32.IsOverlapping`. */
  isOverlapping(id: number): boolean {
    return (this.current & bit(id)) !== 0;
  }
}

/** The general form for any id type (Set-based, `SameValueZero` equality); fresh entries are also
 * listed in record order. Same step protocol as {@link OverlapLatch32}. No allocation per step once
 * the sets have grown. Mirrors C# `OverlapLatch<TId>`. */
export class OverlapLatch<TId> {
  private prev = new Set<TId>();
  private cur = new Set<TId>();
  private readonly enteredIds: TId[] = [];

  /** Fresh entries recorded in the current step, in record order (live; cleared by `begin`). C#
   * `OverlapLatch.Entered`. */
  get entered(): readonly TId[] {
    return this.enteredIds;
  }

  /** Overlaps recorded so far in the current step (live). C# `OverlapLatch.Current`. */
  get current(): ReadonlySet<TId> {
    return this.cur;
  }

  /** Overlaps recorded in the previous step (live). C# `OverlapLatch.Previous`. */
  get previous(): ReadonlySet<TId> {
    return this.prev;
  }

  /** Starts a step: the current overlaps become the previous ones; clears `entered`. C# `OverlapLatch.Begin`. */
  begin(): void {
    const t = this.prev;
    this.prev = this.cur;
    this.cur = t;
    this.cur.clear();
    this.enteredIds.length = 0;
  }

  /** Records an overlap; true when it is fresh. Recording the same id twice in a step reports it fresh
   * at most once. C# `OverlapLatch.Record`. */
  record(id: TId): boolean {
    if (this.cur.has(id)) return false;
    this.cur.add(id);
    if (this.prev.has(id)) return false;
    this.enteredIds.push(id);
    return true;
  }

  /** True when `id` was overlapping in the previous step. C# `OverlapLatch.WasOverlapping`. */
  wasOverlapping(id: TId): boolean {
    return this.prev.has(id);
  }

  /** Forgets everything (both steps). C# `OverlapLatch.Reset`. */
  reset(): void {
    this.prev.clear();
    this.cur.clear();
    this.enteredIds.length = 0;
  }
}
