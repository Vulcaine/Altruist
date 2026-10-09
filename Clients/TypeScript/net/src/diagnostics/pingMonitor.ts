/**
 * Round-trip meter for ping badges (menus and the in-game HUD), fed from two sources:
 *
 * - **HTTP**: while at least one subscriber asks for polling and the page is visible, an empty
 *   request to `url` (e.g. a `GET /api/ping` that answers 204) is timed every few seconds. The
 *   browser's Resource Timing (request sent → first byte) is used when available, because wall
 *   time also counts a busy main thread. The first sample of a burst can pay for DNS / TLS and is
 *   dropped once the next one arrives.
 * - **Socket**: on a live connection the game reports each pong with {@link reportSocket}; that
 *   replaces HTTP polling until it goes stale (`socketStaleMs`).
 *
 * The reading is the median of the last `window` samples, so one slow request does not flash red,
 * bucketed into {@link PingQuality}. Timers are only armed while someone polls.
 *
 * **Paths**: when the client can reach the server more than one way (a direct game host and the
 * page's own origin through a CDN, see `EndpointFailover`), pick the HTTP target per measurement
 * with the `target` option and label socket samples with the path their socket took: samples of
 * different paths never share a window, the reading names its `path`, and `onTargetFailed` can
 * mark a failed primary down and retry the measurement on the next target at once.
 *
 * Use one instance per app. For server-time estimates use {@link ClockSync}; this class only measures delay.
 */
import { Emitter } from '../util/emitter.ts';

/** Bucket of a reading: ≤ goodMs, ≤ badMs, above, no network, or no sample yet. */
export type PingQuality = 'good' | 'ok' | 'bad' | 'offline' | 'unknown';
/** Where the latest sample came from. */
export type PingSource = 'http' | 'socket';

/** Where one HTTP ping goes, and the network path it measures (null: a single, unnamed path). */
export interface PingTarget {
  url: string;
  path: string | null;
}

/** The current reading. */
export interface PingReading {
  /** Median round trip in ms (rounded), null before the first sample or while offline. */
  ms: number | null;
  quality: PingQuality;
  source: PingSource | null;
  /** `now()` of the latest sample. */
  at: number;
  /** Median share of `ms` a {@link NetConditioner} simulated (0 when off): `ms - simMs` ≈ the real network. */
  simMs: number;
  /** Network path of the samples (see {@link PingTarget}); null before the first or when unnamed. */
  path: string | null;
}

/** Options of {@link PingMonitor}. All times in ms. */
export interface PingMonitorOptions {
  /** HTTP ping URL (default `/api/ping`); a `?n=` counter is appended per request. */
  url?: string;
  /** Picks the HTTP target per measurement (overrides `url`), e.g. the direct host while it is up. */
  target?: () => PingTarget;
  /**
   * A measurement at `target` failed (no answer, or an error status other than 429). Return true
   * when that changed the next target (e.g. `EndpointFailover.markDown()` for a failed primary):
   * the measurement is then retried there once before the reading goes offline. Default: no retry.
   */
  onTargetFailed?: (target: PingTarget) => boolean;
  /** Up to this RTT counts as `good` (default 70). */
  goodMs?: number;
  /** Above this RTT counts as `bad` (default 140). */
  badMs?: number;
  /** Samples in the median window (default 5). */
  window?: number;
  /** Poll period of the first `fastSamples` samples (default 600). */
  fastPollMs?: number;
  fastSamples?: number;
  /** Poll period afterwards (default 4000). */
  pollMs?: number;
  /** HTTP request timeout (default 4000). */
  timeoutMs?: number;
  /** Socket samples older than this hand back to HTTP polling (default 12000). */
  socketStaleMs?: number;
  now?: () => number;
  fetch?: (url: string, init: RequestInit) => Promise<Response>;
  /** Simulated RTT added to each HTTP sample (e.g. `() => conditioner.sampleRtt()`). */
  simulatedRtt?: () => number;
}

/** Quality bucket of a round trip with the given thresholds. */
export function pingQuality(ms: number | null, goodMs = 70, badMs = 140): PingQuality {
  if (ms === null) return 'unknown';
  return ms <= goodMs ? 'good' : ms <= badMs ? 'ok' : 'bad';
}

/** Median of a non-empty list (mean of the middle two for even lengths). */
export function median(xs: readonly number[]): number {
  const s = [...xs].sort((a, b) => a - b);
  const m = s.length >> 1;
  return s.length % 2 ? s[m]! : (s[m - 1]! + s[m]!) / 2;
}

/**
 * See the module comment.
 *
 * @example
 * ```ts
 * const ping = new PingMonitor({ url: '/api/ping' });
 * const off = ping.subscribe((r) => badge.show(r.ms, r.quality), { poll: true });
 * socket.packets.on(Pong, (p) => ping.reportSocket(performance.now() - p.clientTime));
 * ```
 */
export class PingMonitor {
  private samples: number[] = [];
  private sims: number[] = [];
  private source: PingSource | null = null;
  private path: string | null = null;
  private offline = false;
  private at = 0;
  private lastSocket = -Infinity;
  private readonly events = new Emitter<{ reading: [PingReading] }>();
  private pollers = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private inFlight = false;
  private sent = 0;
  private coldSample = false;
  private seq = 0;
  private readonly o: Required<Omit<PingMonitorOptions, 'fetch' | 'target' | 'onTargetFailed'>> & Pick<PingMonitorOptions, 'fetch' | 'target' | 'onTargetFailed'>;

  /** Creates the meter (idle until someone subscribes with `poll` or reports a socket sample). */
  constructor(options: PingMonitorOptions = {}) {
    this.o = {
      url: options.url ?? '/api/ping',
      goodMs: options.goodMs ?? 70,
      badMs: options.badMs ?? 140,
      window: options.window ?? 5,
      fastPollMs: options.fastPollMs ?? 600,
      fastSamples: options.fastSamples ?? 3,
      pollMs: options.pollMs ?? 4000,
      timeoutMs: options.timeoutMs ?? 4000,
      socketStaleMs: options.socketStaleMs ?? 12_000,
      now: options.now ?? (() => performance.now()),
      simulatedRtt: options.simulatedRtt ?? (() => 0),
      fetch: options.fetch,
      target: options.target,
      onTargetFailed: options.onTargetFailed,
    };
  }

  /** The current reading. */
  get reading(): PingReading {
    const ms = this.offline || !this.samples.length ? null : Math.round(median(this.samples));
    const simMs = this.offline || !this.sims.length ? 0 : Math.round(median(this.sims));
    return { ms, quality: this.offline ? 'offline' : pingQuality(ms, this.o.goodMs, this.o.badMs), source: this.source, at: this.at, simMs, path: this.path };
  }

  /**
   * Listens for readings (called right away with the current one). `poll: true` keeps the HTTP
   * meter running while subscribed; in-game badges usually leave it off and rely on socket pongs.
   * Returns the unsubscribe function (idempotent).
   */
  subscribe(listener: (r: PingReading) => void, opts: { poll?: boolean } = {}): () => void {
    const off = this.events.on('reading', listener);
    listener(this.reading);
    if (opts.poll) this.addPoller();
    let done = false;
    return () => {
      if (done) return;
      done = true;
      off();
      if (opts.poll) this.removePoller();
    };
  }

  /**
   * A round trip measured on the socket (ping → pong), ms. `simMs`: the part a conditioner
   * simulated (the dispatcher's `ctx.simulatedMs`); `path`: the network path that socket took
   * (see {@link PingTarget}). Invalid values are ignored.
   */
  reportSocket(rttMs: number, simMs = 0, path: string | null = null): void {
    if (!Number.isFinite(rttMs) || rttMs < 0) return;
    this.lastSocket = this.o.now();
    this.push('socket', rttMs, Math.min(rttMs, Math.max(0, simMs)), path);
  }

  /** Starts a fresh window (e.g. the conditioner changed): the reading follows within a sample or two. */
  restart(): void {
    this.samples = [];
    this.sims = [];
    this.sent = 0;
    if (this.pollers && !this.inFlight) this.schedule(0);
  }

  /** Stops polling and forgets listeners (tests, teardown). */
  dispose(): void {
    this.pollers = 0;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.events.clear();
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', this.onVisibility);
  }

  /**
   * Takes one HTTP sample now (also used by the poll loop), retrying once on the next target when
   * `onTargetFailed` says it changed. Resolves when done.
   */
  async measure(): Promise<void> {
    if (this.inFlight) return;
    this.inFlight = true;
    try {
      for (let attempt = 0; attempt < 2; attempt++) {
        const target = this.currentTarget();
        const sampled = await this.measureAt(target);
        if (sampled !== false) return;
        if (attempt > 0 || !(this.o.onTargetFailed?.(target) ?? false)) break;
      }
      this.goOffline();
    } finally {
      this.inFlight = false;
    }
  }

  private currentTarget(): PingTarget {
    return this.o.target ? this.o.target() : { url: this.o.url, path: null };
  }

  /** One timed request: true = sampled, null = rate-limited (no sample, not offline), false = failed. */
  private async measureAt(target: PingTarget): Promise<boolean | null> {
    const ctrl = new AbortController();
    const timeout = setTimeout(() => ctrl.abort(), this.o.timeoutMs);
    const url = `${target.url}${target.url.includes('?') ? '&' : '?'}n=${++this.seq}`;
    const timing = watchTiming(url);
    const t0 = this.o.now();
    try {
      const f = this.o.fetch ?? ((u: string, i: RequestInit) => fetch(u, i));
      const res = await f(url, { cache: 'no-store', credentials: 'omit', signal: ctrl.signal });
      const wall = this.o.now() - t0;
      if (!res.ok) return res.status === 429 ? null : false;
      const real = (await timing.result()) ?? wall;
      const sim = Math.max(0, this.o.simulatedRtt());
      // A new path pays for DNS / TLS / a cold connection again.
      if (this.source === 'http' && this.path !== target.path) this.sent = 0;
      if (this.coldSample && this.samples.length === 1 && this.source === 'http' && this.path === target.path) {
        this.samples = [];
        this.sims = [];
      }
      this.coldSample = this.sent++ === 0;
      this.push('http', real + sim, sim, target.path);
      return true;
    } catch {
      return false;
    } finally {
      timing.stop();
      clearTimeout(timeout);
    }
  }

  private push(source: PingSource, ms: number, simMs: number, path: string | null): void {
    // Another source or another path measures something else: start a fresh window.
    if (this.source !== source || this.path !== path || this.offline) {
      this.samples = [];
      this.sims = [];
    }
    this.source = source;
    this.path = path;
    this.offline = false;
    this.samples.push(ms);
    this.sims.push(simMs);
    while (this.samples.length > this.o.window) this.samples.shift();
    while (this.sims.length > this.o.window) this.sims.shift();
    this.at = this.o.now();
    this.events.emit('reading', this.reading);
  }

  private goOffline(): void {
    if (this.o.now() - this.lastSocket < this.o.socketStaleMs) return;
    this.offline = true;
    this.samples = [];
    this.sims = [];
    this.at = this.o.now();
    this.events.emit('reading', this.reading);
  }

  private addPoller(): void {
    if (this.pollers++ === 0) {
      this.sent = 0;
      if (typeof document !== 'undefined') document.addEventListener('visibilitychange', this.onVisibility);
      this.schedule(0);
    }
  }

  private removePoller(): void {
    if (--this.pollers > 0) return;
    this.pollers = 0;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', this.onVisibility);
  }

  private readonly onVisibility = (): void => {
    // Hidden tabs throttle timers and skew timings: drop the window and start fresh on return.
    if (document.visibilityState !== 'visible' || !this.pollers) return;
    if (this.source === 'http') {
      this.samples = [];
      this.sims = [];
    }
    this.sent = 0;
    this.schedule(0);
  };

  private schedule(ms: number): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.tick();
    }, ms);
  }

  private async tick(): Promise<void> {
    if (!this.pollers) return;
    const hidden = typeof document !== 'undefined' && document.visibilityState === 'hidden';
    const socketLive = this.o.now() - this.lastSocket < this.o.socketStaleMs;
    if (!hidden && !socketLive && !this.inFlight) await this.measure();
    if (this.pollers) this.schedule(this.sent < this.o.fastSamples ? this.o.fastPollMs : this.o.pollMs);
  }
}

/** Resource Timing for one URL (a PerformanceObserver still sees entries after the buffer is full). */
function watchTiming(url: string): { result(): Promise<number | null>; stop(): void } {
  const none = { result: async () => null, stop: () => undefined };
  const loc = (globalThis as { location?: { href: string } }).location;
  if (!loc || typeof PerformanceObserver === 'undefined' || !PerformanceObserver.supportedEntryTypes?.includes('resource')) return none;
  const abs = new URL(url, loc.href).href;
  let found: number | null = null;
  let wake: (() => void) | null = null;
  const obs = new PerformanceObserver((list) => {
    for (const e of list.getEntriesByName(abs) as PerformanceResourceTiming[]) {
      const net = e.requestStart > 0 && e.responseStart >= e.requestStart ? e.responseStart - e.requestStart : e.duration;
      if (net > 0) found = net;
    }
    if (found !== null) wake?.();
  });
  obs.observe({ type: 'resource' });
  return {
    result: () =>
      found !== null
        ? Promise.resolve(found)
        : new Promise((resolve) => {
            const t = setTimeout(() => resolve(found), 250);
            wake = () => {
              clearTimeout(t);
              resolve(found);
            };
          }),
    stop: () => obs.disconnect(),
  };
}
