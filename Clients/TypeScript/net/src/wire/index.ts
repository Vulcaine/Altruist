/**
 * `@altruist/net/wire` — the Altruist wire format: a dependency-free MessagePack codec
 * ({@link encodeMsgPack} / {@link decodeMsgPack}), server envelopes ({@link peekMessageCode},
 * {@link extractMessage}, {@link decodeEnvelope}), client frames ({@link clientFrame},
 * {@link packetFrame}) and code-based routing ({@link PacketDispatcher}, {@link serverPacket}).
 */
export * from './msgpack.ts';
export * from './envelope.ts';
export * from './packetDispatcher.ts';
