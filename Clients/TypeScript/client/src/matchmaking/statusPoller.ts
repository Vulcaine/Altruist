/**
 * `StatusPoller` — keeps a server status fresh while someone looks at it: fetches on demand
 * (single-flight), polls every `activeMs` while the value is "busy" (a match can be rejoined, a
 * queue ban is counting down) and every `idleMs` otherwise, only while subscribed. Failures keep
 * the last value. Between polls, {@link ageSeconds} lets the UI count clocks down locally
 * ({@link remainingSeconds}).
 *
 * Use it for "is there a match to rejoin?" / ban / queue-size endpoints polled by a menu. For
 * pushed state use socket packets instead. Timers via `setTimeout`; async fetches.
 *
 * @example
 * ```ts
 * const status = new StatusPoller({
 *   fetch: () => api.get<{ match: ActiveMatch | null; banSeconds: number }>('/match/active'),
 *   initial: { match: null, banSeconds: 0 },
 *   isBusy: (s, age) => s.match !== null || remainingSeconds(s.banSeconds, age) > 0,
 * });
 * const off = status.subscribe((s) => hub.render(s, status.ageSeconds));
 * ```
 */
import { Emitter } from '../../../net/src/util/emitter.ts';

/** Options of {@link StatusPoller}. Times in ms. */
export interface StatusPollerOptions<T> {
  /** Fetches the status (rejections keep the last value). */
  fetch: () => Promise<T>;
  /** Value before the first fetch. */
  initial: T;
  /** Poll period while busy (default 5000). */
  activeMs?: number;
  /** Poll period otherwise (default 20000). */
  idleMs?: number;
  /** Whether the value counts as busy (`ageSeconds`: time since it was fetched). Default: never. */
  isBusy?: (value: T, ageSeconds: number) => boolean;
  /** Whether fetching is allowed now (e.g. signed in). Default: always. */
  enabled?: () => boolean;
  /** Called after every successful fetch. */
  onFetched?: (value: T) => void;
  now?: () => number;
}

/** `seconds` counted down by `ageSeconds`, never below 0. */
export function remainingSeconds(seconds: number, ageSeconds: number): number {
  return Math.max(0, seconds - ageSeconds);
}

/** See the module comment. */
export class StatusPoller<T> {
  private v: T;
  private at: number;
  private readonly events = new Emitter<{ change: [T] }>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inflight: Promise<void> | null = null;
  private readonly o: StatusPollerOptions<T>;

  /** Creates the poller (no fetch until {@link refresh} or {@link subscribe}). */
  constructor(options: StatusPollerOptions<T>) {
    this.o = options;
    this.v = options.initial;
    this.at = this.now();
  }

  private now(): number {
    return (this.o.now ?? (() => (typeof performance !== 'undefined' ? performance.now() : Date.now())))();
  }

  /** The last fetched (or {@link set}) value. */
  get value(): T {
    return this.v;
  }

  /** Seconds since the value was fetched or set. */
  get ageSeconds(): number {
    return (this.now() - this.at) / 1000;
  }

  /** True when the value counts as busy right now. */
  get busy(): boolean {
    return this.o.isBusy?.(this.v, this.ageSeconds) ?? false;
  }

  /** Replaces the value locally (an answer from another source, e.g. a socket said "banned"); notifies listeners. */
  set(value: T): void {
    this.v = value;
    this.at = this.now();
    this.events.emit('change', value);
    this.schedule();
  }

  /** Fetches now; concurrent callers share one request. Resolves when done (never rejects). */
  refresh(): Promise<void> {
    if (this.o.enabled && !this.o.enabled()) return Promise.resolve();
    this.inflight ??= this.o
      .fetch()
      .then((value) => {
        if (this.o.enabled && !this.o.enabled()) return;
        this.v = value;
        this.at = this.now();
        this.events.emit('change', value);
        this.o.onFetched?.(value);
      })
      .catch(() => undefined)
      .finally(() => {
        this.inflight = null;
        this.schedule();
      });
    return this.inflight;
  }

  /** Listens for changes and keeps polling while at least one listener is subscribed. */
  subscribe(listener: (value: T) => void): () => void {
    const off = this.events.on('change', listener);
    this.schedule();
    return () => {
      off();
      if (this.events.listenerCount('change') === 0) this.stop();
    };
  }

  /** Stops the poll timer (subscriptions stay; the next refresh or subscribe restarts it). */
  stop(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private schedule(): void {
    this.stop();
    if (this.events.listenerCount('change') === 0 || (this.o.enabled && !this.o.enabled())) return;
    this.timer = setTimeout(
      () => {
        this.timer = null;
        void this.refresh();
      },
      this.busy ? (this.o.activeMs ?? 5000) : (this.o.idleMs ?? 20_000),
    );
  }
}
