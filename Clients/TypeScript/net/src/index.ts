/**
 * @altruist/net — TypeScript twin of Altruist's client-facing networking contracts.
 * Snapshot schemas: decode the server's positional snapshot rows and packets from the JSON its
 * C# `SnapshotSchemaSet` exports (`SnapshotSchema`, `rowDecoder`), so field positions are never
 * written by hand on the client.
 */
export * from './snapshotSchema.ts';
