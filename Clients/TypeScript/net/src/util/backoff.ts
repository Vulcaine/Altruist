/**
 * Exponential backoff for retries (socket reconnects, HTTP retries, polling after errors).
 *
 * `delay(n) = min(maxMs, baseMs * factor^n)`, then reduced by up to `jitter` (a 0..1 share) times
 * a random draw: `delay * (1 - jitter * random())`. The defaults (400 ms, ×2, 4 s cap, no jitter)
 * give 400, 800, 1600, 3200, 4000, 4000 ms — a short outage reconnects fast and a long one does
 * not hammer the server.
 *
 * Use jitter when many clients may retry at once (a server restart drops everyone): 0.5 spreads
 * them over half the delay. Pass `random` (e.g. a seeded generator) for deterministic tests.
 * Pure functions plus a tiny counter class; no timers.
 */

/** Settings of {@link backoffDelay} / {@link Backoff}. All times in milliseconds. */
export interface BackoffOptions {
  /** Delay of attempt 0 (default 400). */
  baseMs?: number;
  /** Growth per attempt (default 2). */
  factor?: number;
  /** Upper bound before jitter (default 4000). */
  maxMs?: number;
  /** Share 0..1 of the delay that is randomly removed (default 0: deterministic). */
  jitter?: number;
  /** Random source in [0, 1) (default `Math.random`). */
  random?: () => number;
}

/**
 * Delay before retry `attempt` (0-based), in ms. See the module comment for the formula.
 *
 * @example
 * ```ts
 * backoffDelay(0); // 400
 * backoffDelay(3); // 3200
 * backoffDelay(9); // 4000 (capped)
 * backoffDelay(2, { jitter: 0.5, random: () => 0.5 }); // 1600 * 0.75 = 1200
 * ```
 */
export function backoffDelay(attempt: number, options: BackoffOptions = {}): number {
  const base = options.baseMs ?? 400;
  const factor = options.factor ?? 2;
  const max = options.maxMs ?? 4000;
  const jitter = Math.min(1, Math.max(0, options.jitter ?? 0));
  const n = Math.max(0, Math.floor(attempt));
  const raw = Math.min(max, base * factor ** n);
  if (jitter === 0) return raw;
  const r = (options.random ?? Math.random)();
  return raw * (1 - jitter * r);
}

/**
 * Attempt counter over {@link backoffDelay}: `next()` returns the delay for the current attempt
 * and advances; `reset()` after a success. Use it when the retry loop lives in your code; the
 * {@link AltruistSocket} already has one built in.
 */
export class Backoff {
  private n = 0;

  private readonly options: BackoffOptions;

  /** Creates a counter with the given delay settings. */
  constructor(options: BackoffOptions = {}) {
    this.options = options;
  }

  /** Attempts made since the last {@link reset}. */
  get attempt(): number {
    return this.n;
  }

  /** The delay for the next attempt (ms); advances the counter. */
  next(): number {
    return backoffDelay(this.n++, this.options);
  }

  /** Starts over at attempt 0. */
  reset(): void {
    this.n = 0;
  }
}
