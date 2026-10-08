/**
 * Flow layer. Where simulation code reports what happened — mirror of C# `IEventSink<T>`,
 * `EventSink<T>` and `NullEventSink<T>`. `push` is an alias of `emit`, so code written against an
 * array (`events.push(e)`) works unchanged after switching the parameter type.
 */
export interface EventSinkLike<T> {
  emit(e: T): void;
  push(e: T): void;
}

/** Collects events in emission order; `clear()` each tick reuses the buffer. */
export class EventSink<T> implements EventSinkLike<T>, Iterable<T> {
  private readonly list: T[] = [];

  /** The collected events (emission order). Valid until the next `clear` / `take`. */
  get items(): readonly T[] {
    return this.list;
  }

  get count(): number {
    return this.list.length;
  }

  emit(e: T): void {
    this.list.push(e);
  }

  push(e: T): void {
    this.list.push(e);
  }

  clear(): void {
    this.list.length = 0;
  }

  /** A copy of the collected events, then clears (hand a tick's events on). */
  take(): T[] {
    const out = this.list.slice();
    this.list.length = 0;
    return out;
  }

  [Symbol.iterator](): Iterator<T> {
    return this.list[Symbol.iterator]();
  }
}

/** Drops every event (scratch simulations). */
export const nullEventSink: EventSinkLike<unknown> = {
  emit(): void {},
  push(): void {},
};
