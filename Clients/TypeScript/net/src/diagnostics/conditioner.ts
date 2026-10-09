/**
 * Network conditioner (a developer tool): makes a socket behave like a slower connection so
 * prediction, reconciliation and the HUD can be tested at high ping from a fast one. Plug it into
 * {@link AltruistSocket} (`conditioner` option); keep it out of production builds or behind an
 * admin gate ({@link NetConditioner.setAllowed}).
 *
 * Every message goes through a {@link DelayLine} per direction. The target round trip is split
 * half each way; jitter adds a random ± offset per message (half each way). A WebSocket is a TCP
 * stream, which never reorders or drops: a message is released no earlier than its own time and
 * never before the one ahead of it, so a late message holds back the ones behind it and they
 * arrive in a burst. "Loss" is modelled the same way: a lost TCP segment is retransmitted, so the
 * application sees a stall (at least `minStallMs`, Linux's minimum retransmission timeout, or one
 * RTT) followed by everything queued behind it. Nothing is ever dropped or duplicated, and
 * changing the settings mid-stream keeps queued messages on their schedule.
 *
 * Timers come from a {@link ConditionerClock} (injectable for tests); randomness from `random`.
 */
import { JsonStore, type StorageLike } from '../util/jsonStore.ts';
import { Emitter } from '../util/emitter.ts';

/** Conditioner settings (persisted per device when a storage key is given). */
export interface NetSimSettings {
  enabled: boolean;
  /** Added round trip, ms (half each way). */
  rttMs: number;
  /** ± ms on the round trip (± half per direction), random per message. */
  jitterMs: number;
  /** Share of messages (0..100 %) that stall like a retransmitted TCP segment. */
  lossPct: number;
}

/** Limits and defaults of {@link NetConditioner}. */
export interface NetSimLimits {
  maxRttMs: number;
  maxJitterMs: number;
  maxLossPct: number;
  /** Minimum stall of a "lost" message (ms). */
  minStallMs: number;
}

/** Default limits: 500 ms RTT, 100 ms jitter, 5 % loss, 200 ms minimum stall. */
export const NETSIM_LIMITS: NetSimLimits = { maxRttMs: 500, maxJitterMs: 100, maxLossPct: 5, minStallMs: 200 };
/** Default settings: off, 150 ms when turned on. */
export const NETSIM_DEFAULTS: NetSimSettings = { enabled: false, rttMs: 150, jitterMs: 0, lossPct: 0 };

/** Clamps and rounds raw settings (from storage, a form) into valid ones. */
export function normalizeNetSim(raw: unknown, limits: NetSimLimits = NETSIM_LIMITS, defaults: NetSimSettings = NETSIM_DEFAULTS): NetSimSettings {
  const o = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<keyof NetSimSettings, unknown>>;
  const clamp = (v: unknown, hi: number, fallback: number) => Math.min(hi, Math.max(0, typeof v === 'number' && Number.isFinite(v) ? v : fallback));
  return {
    enabled: o.enabled === true,
    rttMs: Math.round(clamp(o.rttMs, limits.maxRttMs, defaults.rttMs)),
    jitterMs: Math.round(clamp(o.jitterMs, limits.maxJitterMs, defaults.jitterMs)),
    lossPct: Math.round(clamp(o.lossPct, limits.maxLossPct, defaults.lossPct) * 10) / 10,
  };
}

/** "SIM +150 ms" / "SIM +150 ±20 ms · 1% stalls" for badges. */
export function netSimLabel(s: NetSimSettings): string {
  return `SIM +${s.rttMs}${s.jitterMs ? ` ±${s.jitterMs}` : ''} ms${s.lossPct ? ` · ${s.lossPct}% stalls` : ''}`;
}

/** Timer source of a {@link DelayLine} (ms). */
export interface ConditionerClock {
  now(): number;
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

/** `performance.now()` + global timers. */
export const realClock: ConditionerClock = {
  now: () => (typeof performance !== 'undefined' ? performance.now() : Date.now()),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (h) => clearTimeout(h as ReturnType<typeof setTimeout>),
};

/**
 * One direction of one stream: FIFO, released in order by a single timer, each item no earlier
 * than its scheduled time. With nothing queued and no delay (`delay()` returns null) it is a
 * synchronous pass-through. Use it directly to condition any other channel.
 */
export class DelayLine<T> {
  private readonly queue: { item: T; at: number; queued: number }[] = [];
  private last = -Infinity;
  private timer: unknown = null;
  /** How long the most recently released item was held, ms (0 for pass-through). */
  lastHeld = 0;

  /**
   * @param delay One-way delay for the next item in ms, or null for pass-through.
   * @param deliver Receives each item and how long it was held.
   */
  private readonly delay: () => number | null;
  private readonly deliver: (item: T, heldMs: number) => void;
  private readonly clock: ConditionerClock;

  constructor(delay: () => number | null, deliver: (item: T, heldMs: number) => void, clock: ConditionerClock = realClock) {
    this.delay = delay;
    this.deliver = deliver;
    this.clock = clock;
  }

  /** Items waiting. */
  get pending(): number {
    return this.queue.length;
  }

  /** Queues (or passes through) an item. */
  push(item: T): void {
    const d = this.delay();
    const now = this.clock.now();
    if (d === null && !this.queue.length) {
      this.lastHeld = 0;
      this.deliver(item, 0);
      return;
    }
    const at = Math.max(now + (d ?? 0), this.last);
    this.last = at;
    this.queue.push({ item, at, queued: now });
    if (this.timer === null) this.arm();
  }

  /** Releases everything queued now, in order (socket teardown). */
  flush(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
    this.drain(Infinity);
  }

  /** Forgets queued items (the stream is gone). */
  clear(): void {
    if (this.timer !== null) this.clock.clearTimeout(this.timer);
    this.timer = null;
    this.queue.length = 0;
  }

  private arm(): void {
    const head = this.queue[0];
    if (!head) {
      this.timer = null;
      return;
    }
    this.timer = this.clock.setTimeout(
      () => {
        this.timer = null;
        this.drain(this.clock.now());
        this.arm();
      },
      Math.max(0, head.at - this.clock.now()),
    );
  }

  private drain(now: number): void {
    // Timers fire up to ~1 ms early or late; half a ms of slack avoids a 0 ms re-arm loop.
    while (this.queue.length && this.queue[0]!.at <= now + 0.5) {
      const e = this.queue.shift()!;
      const held = Math.max(0, Math.min(now, this.clock.now()) - e.queued);
      this.lastHeld = held;
      this.deliver(e.item, held);
    }
  }
}

/** Options of {@link NetConditioner}. */
export interface NetConditionerOptions {
  /** Persist settings under this key (default: not persisted). */
  storageKey?: string;
  /** Storage for `storageKey` (default localStorage when available). */
  storage?: StorageLike | null;
  /** Random source in [0, 1) (default `Math.random`). */
  random?: () => number;
  clock?: ConditionerClock;
  limits?: Partial<NetSimLimits>;
  /** Initial gate (default true: effective as soon as enabled). */
  allowed?: boolean;
}

/**
 * See the module comment. One instance per app; sockets take delay lines from it with
 * {@link line}, ping meters sample it with {@link sampleRtt}.
 *
 * @example
 * ```ts
 * const conditioner = new NetConditioner({ storageKey: 'mygame.dev.netsim' });
 * conditioner.update({ enabled: true, rttMs: 200, jitterMs: 20 });
 * const socket = new AltruistSocket({ url, conditioner });
 * ```
 */
export class NetConditioner {
  private value: NetSimSettings;
  private allowedFlag: boolean;
  private readonly events = new Emitter<{ change: [NetSimSettings] }>();
  private readonly store: JsonStore<NetSimSettings> | null;
  private readonly random: () => number;
  private readonly clock: ConditionerClock;
  /** Effective limits. */
  readonly limits: NetSimLimits;

  /** Creates the conditioner (settings loaded from storage when `storageKey` is set). */
  constructor(options: NetConditionerOptions = {}) {
    this.limits = { ...NETSIM_LIMITS, ...options.limits };
    this.random = options.random ?? Math.random;
    this.clock = options.clock ?? realClock;
    this.allowedFlag = options.allowed ?? true;
    this.store = options.storageKey
      ? new JsonStore({ key: options.storageKey, version: 1, defaults: NETSIM_DEFAULTS, normalize: (v) => normalizeNetSim(v, this.limits), storage: options.storage })
      : null;
    this.value = this.store ? this.store.load() : { ...NETSIM_DEFAULTS };
  }

  /** A copy of the current settings. */
  get settings(): NetSimSettings {
    return { ...this.value };
  }

  /** The gate (e.g. "is an admin"): settings only take effect while allowed. */
  get allowed(): boolean {
    return this.allowedFlag;
  }

  /** Opens or closes the gate; listeners hear about it when it changes {@link active}. */
  setAllowed(on: boolean): void {
    if (this.allowedFlag === on) return;
    const was = this.active;
    this.allowedFlag = on;
    if (was !== this.active) this.emit();
  }

  /** Conditioning applies right now. */
  get active(): boolean {
    const s = this.value;
    return this.allowedFlag && s.enabled && (s.rttMs > 0 || s.jitterMs > 0 || s.lossPct > 0);
  }

  /** Changes settings (normalized, persisted, applied live). Returns the new settings. */
  update(patch: Partial<NetSimSettings>): NetSimSettings {
    this.value = normalizeNetSim({ ...this.value, ...patch }, this.limits);
    this.store?.save(this.value);
    this.emit();
    return this.settings;
  }

  /** Called on every change (not immediately). */
  subscribe(listener: (s: NetSimSettings) => void): () => void {
    return this.events.on('change', listener);
  }

  /** One-way delay for the next message in ms, or null when off (pass-through). */
  oneWay(): number | null {
    if (!this.active) return null;
    const { rttMs, jitterMs, lossPct } = this.value;
    const jitter = (this.random() * 2 - 1) * (jitterMs / 2);
    const stall = lossPct > 0 && this.random() * 100 < lossPct ? Math.max(this.limits.minStallMs, rttMs) : 0;
    return Math.max(0, rttMs / 2 + jitter) + stall;
  }

  /** A simulated round trip (two one-way samples), 0 when off — for meters that cannot be delayed (HTTP pings). */
  sampleRtt(): number {
    return (this.oneWay() ?? 0) + (this.oneWay() ?? 0);
  }

  /** A delay line driven by this conditioner. */
  line<T>(deliver: (item: T, heldMs: number) => void): DelayLine<T> {
    return new DelayLine<T>(() => this.oneWay(), deliver, this.clock);
  }

  private emit(): void {
    this.events.emit('change', this.settings);
  }
}
