/**
 * `@altruist/net/diagnostics` — connection diagnostics: {@link PingMonitor} (HTTP + socket RTT,
 * median window, quality buckets), {@link ClockSync} (server clock offset from pongs) and the dev
 * {@link NetConditioner} / {@link DelayLine} (latency, jitter and stall simulation).
 */
export * from './pingMonitor.ts';
export * from './clockSync.ts';
export * from './conditioner.ts';
