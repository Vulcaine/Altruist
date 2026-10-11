/**
 * Flow layer. Where simulation code reports what happened — mirror of C# `IEventSink<T>`,
 * `EventSink<T>` and `NullEventSink<T>`. `push` is an alias of `emit`, so code written against an
 * array (`events.push(e)`) works unchanged after switching the parameter type.
 */
/** Where simulation code reports events; accept this type so callers can pass an {@link EventSink}, an
 * array-like collector or {@link nullEventSink}. Mirrors C# `IEventSink<TEvent>`. */
export interface EventSinkLike<T> {
  /** Reports one event. C# `IEventSink.Emit`. */
  emit(e: T): void;
  /** Alias of {@link emit} (array-compatible). */
  push(e: T): void;
}

/** Collects events in emission order; `clear()` each tick reuses the buffer. Mirrors C# `EventSink<TEvent>`.
 * @example
 * ```ts
 * const events = new EventSink<GameEvent>();
 * simulate(state, events); // calls events.emit(...)
 * net.send(events.take());
 * ```
 */
export class EventSink<T> implements EventSinkLike<T>, Iterable<T> {
  private readonly list: T[] = [];

  /** The collected events (emission order). Valid until the next `clear` / `take`. */
  get items(): readonly T[] {
    return this.list;
  }

  /** Number of collected events. C# `EventSink.Count`. */
  get count(): number {
    return this.list.length;
  }

  /** Appends an event. C# `EventSink.Emit`. */
  emit(e: T): void {
    this.list.push(e);
  }

  /** Alias of {@link emit} (C# `EventSink.Add`). */
  push(e: T): void {
    this.list.push(e);
  }

  /** Drops the collected events, keeping the buffer. C# `EventSink.Clear`. */
  clear(): void {
    this.list.length = 0;
  }

  /** A copy of the collected events, then clears (hand a tick's events on). Like C# `ToList()` followed
   * by `Clear()`; see also C# `DrainTo`. */
  take(): T[] {
    const out = this.list.slice();
    this.list.length = 0;
    return out;
  }

  /** Iterates the collected events in emission order. C# `EventSink.GetEnumerator`. */
  [Symbol.iterator](): Iterator<T> {
    return this.list[Symbol.iterator]();
  }
}

/** Drops every event (scratch simulations, predictions). Mirrors C# `NullEventSink<TEvent>.Instance`. */
export const nullEventSink: EventSinkLike<unknown> = {
  emit(): void {},
  push(): void {},
};
