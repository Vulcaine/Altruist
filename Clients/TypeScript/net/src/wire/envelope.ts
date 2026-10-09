/**
 * The two Altruist frame formats, byte for byte:
 *
 * - **Server → client: the envelope** (C# `MessageEnvelope`), a MessagePack array
 *   `[messageCode, header, message]` where `header` is `[timestamp, receiver, sender]`
 *   (C# `PacketHeader`: .NET ticks, receiver client id or nil, sender id, "server" for the server)
 *   and `message` is the packet (a `[Key(n)]` class, i.e. an array whose key 0 is usually the
 *   message code again).
 * - **Client → server: the event-prefixed frame** the server's connection manager reads for binary
 *   codecs: `[u8 eventLength][event UTF-8][MessagePack payload]`, where `event` is the gate name
 *   (1..127 bytes) and the payload is the gate's packet.
 *
 * {@link peekMessageCode} and {@link extractMessage} are exact twins of C#
 * `MessageEnvelopeShape.PeekMessageCode` / `ExtractMessage` (route without decoding); use
 * {@link decodeEnvelope} when you need the whole frame. Pure functions.
 */
import { asBytes, decodeMsgPack, encodeMsgPack, skipMsgPackValue, type BytesLike, type DecodeOptions, type EncodeOptions, type MsgPackError, type f32 } from './msgpack.ts';

/** Routing metadata of an envelope (C# `PacketHeader`). */
export interface PacketHeader {
  /** .NET ticks (100 ns since 0001-01-01 UTC), 0 when never stamped. See {@link ticksToUnixMs}. */
  timestamp: number | bigint;
  /** Target client id; null for broadcasts. */
  receiver: string | null;
  /** Sender id ("server" for server messages). */
  sender: string;
}

/** A decoded server frame. */
export interface Envelope<M = unknown> {
  /** Message code (C# `IPacketBase.MessageCode`). */
  code: number;
  /** Header, or null when the frame carries none / an unexpected shape. */
  header: PacketHeader | null;
  /** The decoded packet (usually an array in `[Key]` order). */
  message: M;
}

/**
 * Message code of an envelope without decoding the rest: element 0 of a fixarray / array16 /
 * array32, encoded as positive fixint, uint8, uint16 or uint32. Returns 0 for anything else
 * (exactly C# `MessageEnvelopeShape.PeekMessageCode`).
 *
 * Use it to route frames cheaply (e.g. a hot snapshot path that only decodes what it needs);
 * {@link PacketDispatcher} calls it for you.
 */
export function peekMessageCode(frame: BytesLike): number {
  const d = asBytes(frame);
  if (d.length < 2) return 0;
  const h = d[0]!;
  let p = 1;
  if ((h & 0xf0) === 0x90) {
    /* fixarray */
  } else if (h === 0xdc) p = 3;
  else if (h === 0xdd) p = 5;
  else return 0;
  if (p >= d.length) return 0;
  const b = d[p]!;
  if (b <= 0x7f) return b;
  if (b === 0xcc && p + 1 < d.length) return d[p + 1]!;
  if (b === 0xcd && p + 2 < d.length) return (d[p + 1]! << 8) | d[p + 2]!;
  if (b === 0xce && p + 4 < d.length) return ((d[p + 1]! << 24) | (d[p + 2]! << 16) | (d[p + 3]! << 8) | d[p + 4]!) >>> 0;
  return 0;
}

/**
 * The packet bytes (element 2) of a fixarray(3) envelope, as a view into `frame` (no copy), or
 * an empty array when the frame is not a `0x93` array or is malformed (exactly C#
 * `MessageEnvelopeShape.ExtractMessage`, except that it returns a view instead of a copy and
 * supports every MessagePack type in the header). Feed the result to {@link decodeMsgPack}.
 */
export function extractMessage(frame: BytesLike): Uint8Array {
  const d = asBytes(frame);
  if (d.length < 3 || d[0] !== 0x93) return new Uint8Array(0);
  let p = skipMsgPackValue(d, 1);
  if (p < 0) return new Uint8Array(0);
  p = skipMsgPackValue(d, p);
  if (p < 0 || p >= d.length) return new Uint8Array(0);
  return d.subarray(p);
}

/**
 * Decodes a whole server frame. Throws {@link MsgPackError} when it is not MessagePack, and
 * `TypeError` when it is not a `[code, header, message]` array with a numeric code.
 *
 * @example
 * ```ts
 * const { code, message } = decodeEnvelope(event.data as ArrayBuffer);
 * ```
 */
export function decodeEnvelope<M = unknown>(frame: BytesLike, options?: DecodeOptions): Envelope<M> {
  const v = decodeMsgPack(frame, options);
  if (!Array.isArray(v) || v.length < 1 || typeof v[0] !== 'number') throw new TypeError('Not an Altruist envelope ([code, header, message]).');
  return { code: v[0], header: toHeader(v[1]), message: v[2] as M };
}

function toHeader(h: unknown): PacketHeader | null {
  if (!Array.isArray(h)) return null;
  const ts = h[0];
  return {
    timestamp: typeof ts === 'number' || typeof ts === 'bigint' ? ts : 0,
    receiver: typeof h[1] === 'string' ? h[1] : null,
    sender: typeof h[2] === 'string' ? h[2] : '',
  };
}

/**
 * Encodes a server frame `[code, [timestamp, receiver, sender], message]` — for mock servers,
 * tests and tools (the server writes these in C#). `header` defaults to the server's unstamped
 * broadcast shape (`0, null, "server"`).
 */
export function encodeEnvelope(code: number, message: unknown, header: Partial<PacketHeader> = {}, options?: EncodeOptions): Uint8Array<ArrayBuffer> {
  return encodeMsgPack([code, [header.timestamp ?? 0, header.receiver ?? null, header.sender ?? 'server'], message], options);
}

const TICKS_AT_UNIX_EPOCH = 621355968000000000n;

/** .NET ticks (header timestamp) → Unix milliseconds. Accepts the lossy number form too. */
export function ticksToUnixMs(ticks: number | bigint): number {
  if (typeof ticks === 'bigint') return Number((ticks - TICKS_AT_UNIX_EPOCH) / 10000n);
  return (ticks - Number(TICKS_AT_UNIX_EPOCH)) / 10000;
}

/** Unix milliseconds → .NET ticks (exact, as bigint). */
export function unixMsToTicks(ms: number): bigint {
  return BigInt(Math.round(ms)) * 10000n + TICKS_AT_UNIX_EPOCH;
}

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();

/** Longest event name the server's binary framing accepts (bytes of UTF-8). */
export const MAX_EVENT_BYTES = 127;

/**
 * Builds a client → server frame `[u8 len][event][payload]`. `payload` is MessagePack-encoded
 * unless it already is bytes (`Uint8Array`, passed through). Throws `RangeError` when the UTF-8
 * event name is empty or longer than {@link MAX_EVENT_BYTES} (the server would misread it).
 *
 * Use {@link packetFrame} for the common `[code, ...fields]` packet shape.
 *
 * @example
 * ```ts
 * socket.send(clientFrame('chat', [1009, 'gg']));
 * ```
 */
export function clientFrame(event: string, payload: unknown, options?: EncodeOptions): Uint8Array<ArrayBuffer> {
  const name = textEncoder.encode(event);
  if (name.length < 1 || name.length > MAX_EVENT_BYTES) throw new RangeError(`Event name must be 1..${MAX_EVENT_BYTES} UTF-8 bytes: '${event}'.`);
  const body = payload instanceof Uint8Array ? payload : encodeMsgPack(payload, options);
  const out = new Uint8Array(1 + name.length + body.length);
  out[0] = name.length;
  out.set(name, 1);
  out.set(body, 1 + name.length);
  return out;
}

/**
 * A packet frame for a gate whose packet is a `[Key]` class with the message code at key 0:
 * `clientFrame(event, [code, ...fields])`. Use {@link f32} on fields that are C# `float`s when
 * the bytes must match a C# client exactly (the server reads either width).
 */
export function packetFrame(event: string, code: number, ...fields: unknown[]): Uint8Array<ArrayBuffer> {
  return clientFrame(event, [code, ...fields]);
}

/**
 * Splits a client → server frame (mock servers, tests, proxies): the event name and the payload
 * bytes (a view). Returns null when the first byte is not a valid event length (0 or ≥ 128, as
 * the server checks) or the frame is too short.
 */
export function parseClientFrame(frame: BytesLike): { event: string; payload: Uint8Array } | null {
  const d = asBytes(frame);
  if (d.length <= 2) return null;
  const n = d[0]!;
  if (n === 0 || n > MAX_EVENT_BYTES || 1 + n > d.length) return null;
  return { event: textDecoder.decode(d.subarray(1, 1 + n)), payload: d.subarray(1 + n) };
}
