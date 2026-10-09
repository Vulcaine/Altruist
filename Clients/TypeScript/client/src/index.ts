/**
 * @altruist/client — the game-client SDK above the wire, built on `@altruist/net`:
 *
 * - **auth** (`@altruist/client/auth`): {@link ApiClient} (in-memory bearer, single-flight cookie
 *   refresh, CSRF header, 401 → refresh → retry) and {@link ApiError} (Altruist `simple` HTTP errors).
 * - **matchmaking** (`@altruist/client/matchmaking`): {@link MatchmakingClient}, the Altruist
 *   match-hosting enums and {@link StatusPoller}.
 * - **pairing** (`@altruist/client/pairing`): {@link PairingHost} / {@link PairingCompanion}, device
 *   pairing over WebRTC with a server-relay path (C# `PairingPortal`).
 *
 * Transport, wire format, diagnostics and small utilities (Emitter, backoff, rate-limit twins,
 * JsonStore) are in `@altruist/net`; import them from there.
 */
export * from './auth/index.ts';
export * from './matchmaking/index.ts';
export * from './pairing/index.ts';
