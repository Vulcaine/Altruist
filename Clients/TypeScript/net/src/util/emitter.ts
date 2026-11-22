/**
 * Typed event emitter: the one listener primitive every Altruist client class uses (sockets, ping
 * meters, auth, matchmaking), so subscriptions look and unsubscribe the same everywhere.
 *
 * Use it for in-process notifications with a fixed set of event names. For server packets use
 * {@link PacketDispatcher} (it routes by message code and decodes once); for DOM events keep
 * `addEventListener`.
 *
 * Synchronous: `emit` calls every listener before it returns, in subscription order, on the
 * caller's stack. A listener added during an emit is not called for that emit; one removed during
 * it is not called if it has not run yet. A throwing listener does not stop the others: its error
 * goes to the `onError` option (default: rethrown asynchronously via `queueMicrotask`, so it
 * still shows up as an uncaught error without breaking the emitter).
 *
 * @example
 * ```ts
 * type Events = { state: [next: string, previous: string]; close: [reason: string] };
 * const events = new Emitter<Events>();
 * const off = events.on('state', (next, prev) => console.log(prev, '→', next));
 * events.emit('state', 'open', 'connecting');
 * off();
 * ```
 */
export type EventMap = { [event: string]: unknown[] };

/** Listener of one event: receives that event's argument tuple. */
export type Listener<A extends unknown[]> = (...args: A) => void;

/** Options of {@link Emitter}. */
export interface EmitterOptions {
  /** Receives errors thrown by listeners (default: rethrown in a microtask). */
  onError?: (error: unknown, event: string) => void;
}

/** See the module comment: a typed, synchronous, error-isolating event emitter. */
export class Emitter<E extends EventMap> {
  private readonly map = new Map<keyof E, Set<Listener<never[]>>>();
  private readonly onError: (error: unknown, event: string) => void;

  /** Creates an emitter; `onError` receives listener exceptions (see {@link EmitterOptions}). */
  constructor(options: EmitterOptions = {}) {
    this.onError =
      options.onError ??
      ((error) =>
        queueMicrotask(() => {
          throw error;
        }));
  }

  /**
   * Subscribes `listener` to `event`. Returns the unsubscribe function (idempotent). Subscribing
   * the same function twice registers it once.
   */
  on<K extends keyof E>(event: K, listener: Listener<E[K]>): () => void {
    let set = this.map.get(event);
    if (!set) this.map.set(event, (set = new Set()));
    set.add(listener as unknown as Listener<never[]>);
    return () => this.off(event, listener);
  }

  /** Like {@link on}, but the listener is removed before its first call. */
  once<K extends keyof E>(event: K, listener: Listener<E[K]>): () => void {
    const off = this.on(event, ((...args: E[K]) => {
      off();
      listener(...args);
    }) as Listener<E[K]>);
    return off;
  }

  /** Removes `listener` from `event` (no-op when not subscribed). */
  off<K extends keyof E>(event: K, listener: Listener<E[K]>): void {
    const set = this.map.get(event);
    if (!set) return;
    set.delete(listener as unknown as Listener<never[]>);
    if (set.size === 0) this.map.delete(event);
  }

  /** Calls every listener of `event` with `args` (see the module comment for ordering and errors). */
  emit<K extends keyof E>(event: K, ...args: E[K]): void {
    const set = this.map.get(event);
    if (!set) return;
    for (const l of [...set]) {
      if (!set.has(l)) continue;
      try {
        (l as unknown as Listener<E[K]>)(...args);
      } catch (error) {
        this.onError(error, String(event));
      }
    }
  }

  /** Listeners currently subscribed to `event`. */
  listenerCount<K extends keyof E>(event: K): number {
    return this.map.get(event)?.size ?? 0;
  }

  /** Removes every listener of `event`, or of every event when omitted. */
  clear(event?: keyof E): void {
    if (event === undefined) this.map.clear();
    else this.map.delete(event);
  }
}
