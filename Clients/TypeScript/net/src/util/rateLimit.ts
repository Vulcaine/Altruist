/**
 * Client-side twins of Altruist's server rate limiters, with the same math, so a client can
 * predict what the server will accept and give feedback ("slow down") before a packet or a request
 * is dropped:
 *
 * - {@link TokenBucket} / {@link GateRateLimiter} ↔ C# `TokenBucketRateLimiter` (realtime packets on
 *   portal gates, config `altruist:server:transport:rate-limit`);
 * - {@link SlidingWindowLimiter} ↔ C# `SlidingWindowCounter` (HTTP policies, config
 *   `altruist:server:http:rate-limit`, answered with 429 + `Retry-After`).
 *
 * Times are milliseconds on the client clock (`now` option, default `performance.now()` /
 * `Date.now()`); the server's math is in seconds and is reproduced exactly (`elapsedMs / 1000`).
 * The client's verdict can still differ from the server's because the two clocks see packets at
 * different times (network jitter): treat it as a guide, not a guarantee. Synchronous, no timers.
 */

const monotonicMs = (): number => (typeof performance !== 'undefined' ? performance.now() : Date.now());

/**
 * One token bucket, refilled continuously: exactly C# `TokenBucketRateLimiter.Bucket.TryTake`.
 * Starts full (the first take sees `capacity` tokens); every take refills
 * `min(capacity, tokens + elapsedSeconds * perSecond)` and needs at least one whole token.
 *
 * Use it to pace your own sends (chat, emotes) to the server's bucket for that gate; for the whole
 * server rule set (named buckets, gates, strikes) use {@link GateRateLimiter}.
 *
 * @example
 * ```ts
 * const chat = new TokenBucket(5, 1); // capacity 5, refills 1 per second, like the server config
 * if (chat.tryTake()) socket.send(chatFrame); else showToast(`Wait ${Math.ceil(chat.waitMs() / 1000)} s`);
 * ```
 */
export class TokenBucket {
  private tokens = -1;
  private last = 0;
  readonly capacity: number;
  readonly perSecond: number;

  /**
   * @param capacity Burst size (negative values clamp to 0, as in C#).
   * @param perSecond Refill rate in tokens per second (negative clamps to 0).
   * @param now Monotonic clock in ms (default `performance.now()`).
   */
  private readonly now: () => number;

  constructor(capacity: number, perSecond: number, now: () => number = monotonicMs) {
    this.now = now;
    this.capacity = Math.max(0, capacity);
    this.perSecond = Math.max(0, perSecond);
  }

  /** Takes one token if there is a whole one (refilling first); true when taken. */
  tryTake(): boolean {
    const now = this.now();
    if (this.tokens < 0) this.tokens = this.capacity;
    else this.tokens = Math.min(this.capacity, this.tokens + ((now - this.last) / 1000) * this.perSecond);
    this.last = now;
    if (this.tokens < 1) return false;
    this.tokens -= 1;
    return true;
  }

  /** Tokens available right now (refilled to the current time, nothing taken). */
  available(): number {
    if (this.tokens < 0) return this.capacity;
    return Math.min(this.capacity, this.tokens + ((this.now() - this.last) / 1000) * this.perSecond);
  }

  /** Milliseconds until a take would succeed (0 = now; `Infinity` when it never refills). */
  waitMs(): number {
    const have = this.available();
    if (have >= 1) return 0;
    if (this.perSecond <= 0 || this.capacity < 1) return Infinity;
    return ((1 - have) / this.perSecond) * 1000;
  }

  /** Back to full (as if never used). */
  reset(): void {
    this.tokens = -1;
  }
}

/** What the server does with a packet (C# `RateVerdict`). */
export type RateVerdict = 'allow' | 'drop' | 'disconnect';

/** One named bucket of {@link GateRateLimiterOptions} (C# `RateBucketOptions`). */
export interface GateBucketOptions {
  capacity: number;
  perSecond: number;
  /** Gates (event names) drawing from this bucket. */
  gates?: readonly string[];
}

/**
 * The server's realtime rate-limit rules for one connection (C# `RateLimitOptions`; same defaults).
 * Copy the values of `altruist:server:transport:rate-limit` (camelCase keys).
 */
export interface GateRateLimiterOptions {
  /** Payloads above this many bytes are rejected before any bucket (default 65536; 0 = no limit). */
  maxPayloadBytes?: number;
  /** Strikes an oversize payload counts (default 20). */
  oversizeStrikes?: number;
  /** Strikes within the window that close the connection (default 60; 0 = never). */
  strikesToDisconnect?: number;
  /** Strike window in seconds (default 10). */
  strikeWindowSeconds?: number;
  /** Bucket for gates no bucket lists (default "default"; a missing bucket = unlimited). */
  defaultBucket?: string;
  /** Named buckets. */
  buckets: Record<string, GateBucketOptions>;
  /**
   * Bucket names of gates set by `[RateLimit("bucket")]` on the server handler (the server reads
   * the attribute; the client has to be told). Gates listed in a bucket's `gates` win.
   */
  gateAttributes?: Record<string, string>;
}

/**
 * Per-gate token buckets with strikes: C# `TokenBucketRateLimiter.Check` for a single connection.
 * Every packet over a bucket's limit is dropped and counts a strike; `strikesToDisconnect` strikes
 * within `strikeWindowSeconds` close the connection, after which every packet is dropped.
 *
 * Use it in a client that sends user-driven packets (chat, pings, emotes) to refuse locally what
 * the server would drop, and never come near the disconnect. For a single bucket use
 * {@link TokenBucket}.
 *
 * @example
 * ```ts
 * const limits = new GateRateLimiter({ buckets: { chat: { capacity: 5, perSecond: 1, gates: ['chat'] } } });
 * if (limits.check('chat', bytes.length) === 'allow') socket.send(frame);
 * ```
 */
export class GateRateLimiter {
  private readonly byGate = new Map<string, { name: string; bucket: TokenBucket }>();
  private readonly byName = new Map<string, { name: string; bucket: TokenBucket }>();
  private readonly def: { name: string; bucket: TokenBucket } | undefined;
  private readonly opts: Required<Omit<GateRateLimiterOptions, 'buckets' | 'gateAttributes'>>;
  private strikeWindowStart: number;
  private strikes = 0;
  private closed = false;

  /** Creates the limiter (buckets start full). `now`: monotonic clock in ms. */
  private readonly now: () => number;

  constructor(options: GateRateLimiterOptions, now: () => number = monotonicMs) {
    this.now = now;
    this.opts = {
      maxPayloadBytes: options.maxPayloadBytes ?? 64 * 1024,
      oversizeStrikes: options.oversizeStrikes ?? 20,
      strikesToDisconnect: options.strikesToDisconnect ?? 60,
      strikeWindowSeconds: options.strikeWindowSeconds ?? 10,
      defaultBucket: options.defaultBucket ?? 'default',
    };
    for (const [name, b] of Object.entries(options.buckets)) {
      const entry = { name, bucket: new TokenBucket(b.capacity, b.perSecond, now) };
      this.byName.set(name, entry);
      for (const gate of b.gates ?? []) if (!this.byGate.has(gate)) this.byGate.set(gate, entry);
    }
    for (const [gate, bucket] of Object.entries(options.gateAttributes ?? {})) {
      const entry = this.byName.get(bucket);
      if (entry && !this.byGate.has(gate)) this.byGate.set(gate, entry);
    }
    this.def = this.byName.get(this.opts.defaultBucket);
    this.strikeWindowStart = now();
  }

  /** Name of the bucket `gate` draws from, or null (unlimited). */
  bucketOf(gate: string): string | null {
    return (this.byGate.get(gate) ?? this.def)?.name ?? null;
  }

  /** The verdict for one packet of `payloadLength` bytes on `gate` (consumes a token when allowed). */
  check(gate: string, payloadLength = 0): RateVerdict {
    const now = this.now();
    if (this.closed) return 'drop';
    if (this.opts.maxPayloadBytes > 0 && payloadLength > this.opts.maxPayloadBytes) return this.strike(now, Math.max(1, this.opts.oversizeStrikes));
    const entry = this.byGate.get(gate) ?? this.def;
    if (!entry) return 'allow';
    if (entry.bucket.tryTake()) return 'allow';
    return this.strike(now, 1);
  }

  /** Milliseconds until `gate` would allow a packet (0 = now). */
  waitMs(gate: string): number {
    const entry = this.byGate.get(gate) ?? this.def;
    return entry ? entry.bucket.waitMs() : 0;
  }

  /** Clears strikes, the closed flag and every bucket (a fresh connection). */
  reset(): void {
    this.closed = false;
    this.strikes = 0;
    this.strikeWindowStart = this.now();
    for (const e of this.byName.values()) e.bucket.reset();
  }

  private strike(now: number, weight: number): RateVerdict {
    if ((now - this.strikeWindowStart) / 1000 > this.opts.strikeWindowSeconds) {
      this.strikeWindowStart = now;
      this.strikes = 0;
    }
    this.strikes += weight;
    if (this.opts.strikesToDisconnect <= 0 || this.strikes < this.opts.strikesToDisconnect) return 'drop';
    this.closed = true;
    return 'disconnect';
  }
}

/** Outcome of one {@link SlidingWindowLimiter.hit} (C# `RateLimitDecision`). */
export interface RateLimitDecision {
  allowed: boolean;
  /** Whole seconds until a hit would be allowed, at least 1 when rejected (the `Retry-After` value); 0 when allowed. */
  retryAfterSeconds: number;
  /** Exact milliseconds until a hit would be allowed (0 when allowed). */
  retryAfterMs: number;
}

/**
 * At most `permit` hits per sliding `windowMs`, per key: exactly C# `SlidingWindowCounter.Hit`
 * (hits at or before `now - window` expire; a rejected hit is not recorded and reports the time
 * until the oldest hit expires, rounded up to whole seconds, at least 1).
 *
 * Use it to mirror an HTTP policy on the client (disable a "resend email" button with a countdown
 * instead of collecting 429s) or to throttle any user action. For packet gates use
 * {@link GateRateLimiter}.
 *
 * @example
 * ```ts
 * const resend = new SlidingWindowLimiter(3, 60 * 60_000); // 3 per hour, like the server policy
 * const d = resend.hit();
 * if (!d.allowed) showError(`Try again in ${d.retryAfterSeconds} s`);
 * ```
 */
export class SlidingWindowLimiter {
  private readonly windows = new Map<string, number[]>();

  /**
   * @param permit Hits allowed per window (must be ≥ 1).
   * @param windowMs Window length in ms (must be > 0).
   * @param now Clock in ms (default `Date.now()`).
   */
  readonly permit: number;
  readonly windowMs: number;
  private readonly now: () => number;

  constructor(permit: number, windowMs: number, now: () => number = () => Date.now()) {
    this.permit = permit;
    this.windowMs = windowMs;
    this.now = now;
    if (!(permit >= 1) || !(windowMs > 0)) throw new RangeError('SlidingWindowLimiter needs permit >= 1 and windowMs > 0.');
  }

  /** Records a hit for `key` when allowed; see the class comment. */
  hit(key = ''): RateLimitDecision {
    const now = this.now();
    const hits = this.prune(key, now);
    if (hits.length >= this.permit) {
      const wait = hits[0]! + this.windowMs - now;
      return { allowed: false, retryAfterMs: Math.max(0, wait), retryAfterSeconds: Math.max(1, Math.ceil(wait / 1000)) };
    }
    hits.push(now);
    this.windows.set(key, hits);
    return { allowed: true, retryAfterMs: 0, retryAfterSeconds: 0 };
  }

  /** Hits `key` still has in the current window. */
  remaining(key = ''): number {
    return Math.max(0, this.permit - this.prune(key, this.now()).length);
  }

  /** Forgets `key` (or every key). */
  reset(key?: string): void {
    if (key === undefined) this.windows.clear();
    else this.windows.delete(key);
  }

  private prune(key: string, now: number): number[] {
    const hits = this.windows.get(key) ?? [];
    const cutoff = now - this.windowMs;
    let i = 0;
    while (i < hits.length && hits[i]! <= cutoff) i++;
    if (i > 0) hits.splice(0, i);
    if (hits.length === 0) this.windows.delete(key);
    return hits;
  }
}
