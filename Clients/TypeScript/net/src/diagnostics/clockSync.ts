/**
 * Server clock estimate from ping / pong exchanges (NTP-style): for each exchange the client
 * records when it sent the ping (`sentAt`) and received the pong (`receivedAt`), both on its own
 * monotonic clock, and the pong carries the server's time when it answered (`serverTime`). Then
 *
 *     rtt    = receivedAt - sentAt
 *     offset = serverTime - (sentAt + receivedAt) / 2      (server clock − client clock)
 *
 * The estimate uses the sample with the smallest round trip among the last `window` ones: the
 * shorter the trip, the smaller the error from asymmetric paths (at most rtt / 2). Units are
 * whatever you feed in; use ms for both clocks (convert a server tick with `tick * dtMs`).
 *
 * Use it to show a server-synced countdown or to map server timestamps to local time. Round-trip
 * display belongs to {@link PingMonitor}. Pure bookkeeping, no timers.
 *
 * @example
 * ```ts
 * const clock = new ClockSync();
 * socket.packets.on(Pong, (p) => clock.addSample(p.clientTime, p.serverTick * 1000 / tickRate, performance.now()));
 * const serverNowMs = clock.toServerTime(performance.now());
 * ```
 */
export interface ClockSample {
  rtt: number;
  offset: number;
  at: number;
}

/** See the module comment. */
export class ClockSync {
  private readonly samples: ClockSample[] = [];

  /** Samples kept. */
  readonly window: number;

  /** @param window Samples kept (default 8). */
  constructor(window = 8) {
    this.window = window;
  }

  /**
   * Adds one exchange. Returns false (ignored) when the values are not finite or the round trip
   * is negative.
   */
  addSample(sentAt: number, serverTime: number, receivedAt: number): boolean {
    const rtt = receivedAt - sentAt;
    if (![sentAt, serverTime, receivedAt].every(Number.isFinite) || rtt < 0) return false;
    this.samples.push({ rtt, offset: serverTime - (sentAt + receivedAt) / 2, at: receivedAt });
    while (this.samples.length > this.window) this.samples.shift();
    return true;
  }

  /** True once at least one sample was added. */
  get ready(): boolean {
    return this.samples.length > 0;
  }

  /** The sample the estimate uses (smallest rtt; the newest wins ties), or null. */
  get best(): ClockSample | null {
    let best: ClockSample | null = null;
    for (const s of this.samples) if (!best || s.rtt <= best.rtt) best = s;
    return best;
  }

  /** Estimated server clock − client clock (0 before the first sample). */
  get offset(): number {
    return this.best?.offset ?? 0;
  }

  /** Round trip of the best sample (0 before the first sample). */
  get rtt(): number {
    return this.best?.rtt ?? 0;
  }

  /** Worst-case error of {@link offset}: half the best round trip. */
  get uncertainty(): number {
    return this.rtt / 2;
  }

  /** Client time → estimated server time. */
  toServerTime(clientTime: number): number {
    return clientTime + this.offset;
  }

  /** Server time → estimated client time. */
  toClientTime(serverTime: number): number {
    return serverTime - this.offset;
  }

  /** Forgets every sample (new server, reconnect to another node). */
  reset(): void {
    this.samples.length = 0;
  }
}
