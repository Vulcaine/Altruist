/**
 * `@altruist/net/util` — small client primitives used across the Altruist client SDK: a typed
 * {@link Emitter}, {@link backoffDelay} / {@link Backoff}, the server rate limiters' client twins
 * ({@link TokenBucket}, {@link GateRateLimiter}, {@link SlidingWindowLimiter}) and a versioned
 * {@link JsonStore}. Dependency-free and usable without the rest of the package.
 */
export * from './emitter.ts';
export * from './backoff.ts';
export * from './rateLimit.ts';
export * from './jsonStore.ts';
