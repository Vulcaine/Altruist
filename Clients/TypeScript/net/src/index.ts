/**
 * @altruist/net — TypeScript twin of Altruist's client-facing networking: everything between a
 * game client and an Altruist server below the game's own packets.
 *
 * - **wire** (`@altruist/net/wire`): MessagePack codec, server envelopes, client frames, packet routing.
 * - **transport** (`@altruist/net/transport`): {@link AltruistSocket}, connection tickets, fleet redirects, failover.
 * - **diagnostics** (`@altruist/net/diagnostics`): {@link PingMonitor}, {@link ClockSync}, {@link NetConditioner}.
 * - **util** (`@altruist/net/util`): {@link Emitter}, backoff, rate-limit twins, {@link JsonStore}.
 * - **snapshot schemas**: decode the server's positional snapshot rows and packets from the JSON its
 *   C# `SnapshotSchemaSet` exports (`SnapshotSchema`, `rowDecoder`), so field positions are never
 *   written by hand on the client.
 *
 * HTTP auth and matchmaking flows live in `@altruist/client`, which builds on this package.
 */
export * from './snapshotSchema.ts';
export * from './wire/index.ts';
export * from './transport/index.ts';
export * from './diagnostics/index.ts';
export * from './util/index.ts';
