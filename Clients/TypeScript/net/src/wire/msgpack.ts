/**
 * Minimal, dependency-free MessagePack encoder / decoder for Altruist's wire format (the server
 * uses MessagePack-CSharp; packets are `[Key(n)]` classes, i.e. arrays).
 *
 * Encoding picks the smallest representation, byte-identical to MessagePack-CSharp for the same
 * values and to `@msgpack/msgpack`'s defaults (so a client can switch without changing a byte):
 * non-negative integers as positive fixint / uint8 / uint16 / uint32 / uint64, negative ones as
 * negative fixint / int8 / int16 / int32 / int64, other numbers as float64 (or float32 with
 * {@link f32} / the `float32` option — what a C# `float` member writes), strings as UTF-8
 * fixstr / str8 / str16 / str32, `Uint8Array` / `ArrayBuffer` as bin, arrays as arrays, `Map` and
 * plain objects as maps, `null` / `undefined` as nil, `bigint` as int64 / uint64,
 * {@link MsgPackExt} as ext.
 *
 * Use {@link encodeMsgPack} / {@link decodeMsgPack} for whole values. To route server frames
 * without decoding them, use {@link peekMessageCode} (envelope.ts). Pure functions; no shared state.
 */

/** Malformed, truncated or unsupported MessagePack data (decode) or an unsupported value (encode). */
export class MsgPackError extends Error {
  /** Creates the error. */
  constructor(message: string) {
    super(message);
    this.name = 'MsgPackError';
  }
}

/** An extension value (type -128..127 and raw data); timestamps (type -1) are left to the caller. */
export class MsgPackExt {
  /** Extension type (-128..127). */
  readonly type: number;
  /** Raw payload. */
  readonly data: Uint8Array;

  /** Creates an ext value. */
  constructor(type: number, data: Uint8Array) {
    this.type = type;
    this.data = data;
  }
}

/** A number that must be written as float32 (`0xca`), like a C# `float` member. Create with {@link f32}. */
export class Float32 {
  /** The number. */
  readonly value: number;

  /** Wraps a value. */
  constructor(value: number) {
    this.value = value;
  }
}

/** Marks `value` to be encoded as float32 (C# `float`) instead of float64 / integer. */
export function f32(value: number): Float32 {
  return new Float32(value);
}

/** Options of {@link encodeMsgPack}. */
export interface EncodeOptions {
  /** Write every non-integer number as float32 instead of float64 (default false). */
  float32?: boolean;
  /** Skip object properties whose value is `undefined` instead of writing nil (default false). */
  ignoreUndefined?: boolean;
}

/** Options of {@link decodeMsgPack}. */
export interface DecodeOptions {
  /**
   * How int64 / uint64 values are returned: `number` (default; exact up to 2^53, like
   * `@msgpack/msgpack`), `bigint` (always), or `auto` (number when exact, else bigint).
   */
  int64?: 'number' | 'bigint' | 'auto';
  /** Return maps as `Map` (any key type) instead of plain objects (default false). */
  mapAsMap?: boolean;
  /** Maximum nesting depth (default 100) — guards against hostile input. */
  maxDepth?: number;
}

const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder('utf-8', { fatal: false });

class Writer {
  buf = new Uint8Array(64);
  view = new DataView(this.buf.buffer);
  pos = 0;

  ensure(n: number): void {
    if (this.pos + n <= this.buf.length) return;
    let size = this.buf.length * 2;
    while (size < this.pos + n) size *= 2;
    const next = new Uint8Array(size);
    next.set(this.buf.subarray(0, this.pos));
    this.buf = next;
    this.view = new DataView(next.buffer);
  }
  u8(v: number): void {
    this.ensure(1);
    this.buf[this.pos++] = v;
  }
  u16(v: number): void {
    this.ensure(2);
    this.view.setUint16(this.pos, v);
    this.pos += 2;
  }
  u32(v: number): void {
    this.ensure(4);
    this.view.setUint32(this.pos, v);
    this.pos += 4;
  }
  bytes(b: Uint8Array): void {
    this.ensure(b.length);
    this.buf.set(b, this.pos);
    this.pos += b.length;
  }
}

/**
 * Encodes `value` to MessagePack bytes (see the module comment for the type mapping).
 * Throws {@link MsgPackError} for unsupported values (functions, symbols) and cycles deeper than 100.
 *
 * @example
 * ```ts
 * encodeMsgPack([1001, 'hello', 3]);          // 93 cd 03 e9 a5 68 65 6c 6c 6f 03
 * encodeMsgPack([f32(1.5)]);                  // 91 ca 3f c0 00 00  (C# float)
 * ```
 */
export function encodeMsgPack(value: unknown, options: EncodeOptions = {}): Uint8Array<ArrayBuffer> {
  const w = new Writer();
  write(w, value, options, 0);
  return w.buf.slice(0, w.pos);
}

function write(w: Writer, v: unknown, o: EncodeOptions, depth: number): void {
  if (depth > 100) throw new MsgPackError('Value nested too deeply (or cyclic).');
  if (v === null || v === undefined) return w.u8(0xc0);
  switch (typeof v) {
    case 'boolean':
      return w.u8(v ? 0xc3 : 0xc2);
    case 'number':
      return writeNumber(w, v, o.float32 === true);
    case 'bigint':
      return writeBigInt(w, v);
    case 'string':
      return writeString(w, v);
    case 'object':
      break;
    default:
      throw new MsgPackError(`Cannot encode a ${typeof v}.`);
  }
  if (v instanceof Float32) {
    w.u8(0xca);
    w.ensure(4);
    w.view.setFloat32(w.pos, v.value);
    w.pos += 4;
    return;
  }
  if (v instanceof Uint8Array || v instanceof ArrayBuffer || ArrayBuffer.isView(v)) {
    const b =
      v instanceof Uint8Array ? v : v instanceof ArrayBuffer ? new Uint8Array(v) : new Uint8Array((v as ArrayBufferView).buffer, (v as ArrayBufferView).byteOffset, (v as ArrayBufferView).byteLength);
    if (b.length <= 0xff) {
      w.u8(0xc4);
      w.u8(b.length);
    } else if (b.length <= 0xffff) {
      w.u8(0xc5);
      w.u16(b.length);
    } else {
      w.u8(0xc6);
      w.u32(b.length);
    }
    return w.bytes(b);
  }
  if (v instanceof MsgPackExt) return writeExt(w, v);
  if (Array.isArray(v)) {
    writeHeader(w, v.length, 0x90, 0xdc, 0xdd, 15);
    for (const item of v) write(w, item, o, depth + 1);
    return;
  }
  if (v instanceof Map) {
    writeHeader(w, v.size, 0x80, 0xde, 0xdf, 15);
    for (const [k, val] of v) {
      write(w, k, o, depth + 1);
      write(w, val, o, depth + 1);
    }
    return;
  }
  const entries = Object.entries(v as Record<string, unknown>).filter(([, val]) => !(o.ignoreUndefined && val === undefined));
  writeHeader(w, entries.length, 0x80, 0xde, 0xdf, 15);
  for (const [k, val] of entries) {
    writeString(w, k);
    write(w, val, o, depth + 1);
  }
}

function writeHeader(w: Writer, n: number, fix: number, h16: number, h32: number, fixMax: number): void {
  if (n <= fixMax) w.u8(fix | n);
  else if (n <= 0xffff) {
    w.u8(h16);
    w.u16(n);
  } else {
    w.u8(h32);
    w.u32(n);
  }
}

function writeNumber(w: Writer, v: number, float32: boolean): void {
  if (Number.isSafeInteger(v)) {
    if (v >= 0) {
      if (v < 0x80) return w.u8(v);
      if (v <= 0xff) return (w.u8(0xcc), w.u8(v));
      if (v <= 0xffff) return (w.u8(0xcd), w.u16(v));
      if (v <= 0xffffffff) return (w.u8(0xce), w.u32(v));
      return writeBigInt(w, BigInt(v));
    }
    if (v >= -0x20) return w.u8(v & 0xff);
    if (v >= -0x80) return (w.u8(0xd0), w.u8(v & 0xff));
    if (v >= -0x8000) return (w.u8(0xd1), w.u16(v & 0xffff));
    if (v >= -0x80000000) return (w.u8(0xd2), w.u32(v >>> 0));
    return writeBigInt(w, BigInt(v));
  }
  if (float32) {
    w.u8(0xca);
    w.ensure(4);
    w.view.setFloat32(w.pos, v);
    w.pos += 4;
    return;
  }
  w.u8(0xcb);
  w.ensure(8);
  w.view.setFloat64(w.pos, v);
  w.pos += 8;
}

function writeBigInt(w: Writer, v: bigint): void {
  if (v >= 0n) {
    if (v <= 0xffffffffn) return writeNumber(w, Number(v), false);
    if (v > 0xffffffffffffffffn) throw new MsgPackError('Integer out of uint64 range.');
    w.u8(0xcf);
    w.ensure(8);
    w.view.setBigUint64(w.pos, v);
    w.pos += 8;
    return;
  }
  if (v >= -0x80000000n) return writeNumber(w, Number(v), false);
  if (v < -0x8000000000000000n) throw new MsgPackError('Integer out of int64 range.');
  w.u8(0xd3);
  w.ensure(8);
  w.view.setBigInt64(w.pos, v);
  w.pos += 8;
}

function writeString(w: Writer, s: string): void {
  const b = textEncoder.encode(s);
  const n = b.length;
  if (n < 32) w.u8(0xa0 | n);
  else if (n <= 0xff) {
    w.u8(0xd9);
    w.u8(n);
  } else if (n <= 0xffff) {
    w.u8(0xda);
    w.u16(n);
  } else {
    w.u8(0xdb);
    w.u32(n);
  }
  w.bytes(b);
}

function writeExt(w: Writer, e: MsgPackExt): void {
  const n = e.data.length;
  const fixed: Record<number, number> = { 1: 0xd4, 2: 0xd5, 4: 0xd6, 8: 0xd7, 16: 0xd8 };
  if (fixed[n] !== undefined) w.u8(fixed[n]!);
  else if (n <= 0xff) {
    w.u8(0xc7);
    w.u8(n);
  } else if (n <= 0xffff) {
    w.u8(0xc8);
    w.u16(n);
  } else {
    w.u8(0xc9);
    w.u32(n);
  }
  w.u8(e.type & 0xff);
  w.bytes(e.data);
}

class Reader {
  pos = 0;
  readonly view: DataView;
  readonly buf: Uint8Array;
  constructor(buf: Uint8Array) {
    this.buf = buf;
    this.view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  }
  need(n: number): void {
    if (this.pos + n > this.buf.length) throw new MsgPackError(`Truncated MessagePack data at byte ${this.pos}.`);
  }
  u8(): number {
    this.need(1);
    return this.buf[this.pos++]!;
  }
  u16(): number {
    this.need(2);
    const v = this.view.getUint16(this.pos);
    this.pos += 2;
    return v;
  }
  u32(): number {
    this.need(4);
    const v = this.view.getUint32(this.pos);
    this.pos += 4;
    return v;
  }
  bytes(n: number): Uint8Array {
    this.need(n);
    const b = this.buf.slice(this.pos, this.pos + n);
    this.pos += n;
    return b;
  }
}

/** Accepts the byte containers a WebSocket or fetch hands out. */
export type BytesLike = Uint8Array | ArrayBuffer | ArrayBufferView;

/** Views any {@link BytesLike} as a `Uint8Array` without copying. */
export function asBytes(data: BytesLike): Uint8Array {
  if (data instanceof Uint8Array) return data;
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
}

/**
 * Decodes one MessagePack value that must span all of `data` (trailing bytes are an error).
 * Integers come back as numbers (see `int64`), floats as numbers, bin as a copied `Uint8Array`,
 * maps as plain objects (keys stringified) or `Map`s, ext as {@link MsgPackExt}.
 * Throws {@link MsgPackError} on malformed or truncated input.
 *
 * @example
 * ```ts
 * const [code, header, message] = decodeMsgPack(frame) as [number, unknown, unknown[]];
 * ```
 */
export function decodeMsgPack(data: BytesLike, options: DecodeOptions = {}): unknown {
  const r = new Reader(asBytes(data));
  const v = read(r, options, 0);
  if (r.pos !== r.buf.length) throw new MsgPackError(`Extra bytes after the MessagePack value (${r.buf.length - r.pos}).`);
  return v;
}

/**
 * Decodes the value starting at `offset` and returns it with the offset just past it (for
 * streams of concatenated values). Throws {@link MsgPackError} like {@link decodeMsgPack}.
 */
export function decodeMsgPackAt(data: BytesLike, offset: number, options: DecodeOptions = {}): { value: unknown; end: number } {
  const r = new Reader(asBytes(data));
  r.pos = offset;
  const value = read(r, options, 0);
  return { value, end: r.pos };
}

function int64(hi: bigint, mode: DecodeOptions['int64']): number | bigint {
  if (mode === 'bigint') return hi;
  if (mode === 'auto') return hi >= BigInt(Number.MIN_SAFE_INTEGER) && hi <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(hi) : hi;
  return Number(hi);
}

function read(r: Reader, o: DecodeOptions, depth: number): unknown {
  if (depth > (o.maxDepth ?? 100)) throw new MsgPackError('MessagePack data nested too deeply.');
  const b = r.u8();
  if (b <= 0x7f) return b;
  if (b >= 0xe0) return b - 0x100;
  if ((b & 0xe0) === 0xa0) return str(r, b & 0x1f);
  if ((b & 0xf0) === 0x90) return arr(r, b & 0x0f, o, depth);
  if ((b & 0xf0) === 0x80) return map(r, b & 0x0f, o, depth);
  switch (b) {
    case 0xc0:
      return null;
    case 0xc2:
      return false;
    case 0xc3:
      return true;
    case 0xcc:
      return r.u8();
    case 0xcd:
      return r.u16();
    case 0xce:
      return r.u32();
    case 0xcf: {
      r.need(8);
      const v = r.view.getBigUint64(r.pos);
      r.pos += 8;
      return int64(v, o.int64);
    }
    case 0xd0: {
      const v = r.u8();
      return v > 0x7f ? v - 0x100 : v;
    }
    case 0xd1: {
      const v = r.u16();
      return v > 0x7fff ? v - 0x10000 : v;
    }
    case 0xd2:
      return r.u32() | 0;
    case 0xd3: {
      r.need(8);
      const v = r.view.getBigInt64(r.pos);
      r.pos += 8;
      return int64(v, o.int64);
    }
    case 0xca: {
      r.need(4);
      const v = r.view.getFloat32(r.pos);
      r.pos += 4;
      return v;
    }
    case 0xcb: {
      r.need(8);
      const v = r.view.getFloat64(r.pos);
      r.pos += 8;
      return v;
    }
    case 0xd9:
      return str(r, r.u8());
    case 0xda:
      return str(r, r.u16());
    case 0xdb:
      return str(r, r.u32());
    case 0xc4:
      return r.bytes(r.u8());
    case 0xc5:
      return r.bytes(r.u16());
    case 0xc6:
      return r.bytes(r.u32());
    case 0xdc:
      return arr(r, r.u16(), o, depth);
    case 0xdd:
      return arr(r, r.u32(), o, depth);
    case 0xde:
      return map(r, r.u16(), o, depth);
    case 0xdf:
      return map(r, r.u32(), o, depth);
    case 0xd4:
      return ext(r, 1);
    case 0xd5:
      return ext(r, 2);
    case 0xd6:
      return ext(r, 4);
    case 0xd7:
      return ext(r, 8);
    case 0xd8:
      return ext(r, 16);
    case 0xc7:
      return ext(r, r.u8());
    case 0xc8:
      return ext(r, r.u16());
    case 0xc9:
      return ext(r, r.u32());
    default:
      throw new MsgPackError(`Invalid MessagePack type byte 0x${b.toString(16)} at ${r.pos - 1}.`);
  }
}

function str(r: Reader, n: number): string {
  r.need(n);
  const s = textDecoder.decode(r.buf.subarray(r.pos, r.pos + n));
  r.pos += n;
  return s;
}

function arr(r: Reader, n: number, o: DecodeOptions, depth: number): unknown[] {
  // Every element needs at least one byte: reject absurd lengths before allocating.
  r.need(n);
  const out = new Array<unknown>(n);
  for (let i = 0; i < n; i++) out[i] = read(r, o, depth + 1);
  return out;
}

function map(r: Reader, n: number, o: DecodeOptions, depth: number): unknown {
  r.need(n * 2);
  if (o.mapAsMap) {
    const m = new Map<unknown, unknown>();
    for (let i = 0; i < n; i++) {
      const k = read(r, o, depth + 1);
      m.set(k, read(r, o, depth + 1));
    }
    return m;
  }
  const obj: Record<string, unknown> = {};
  for (let i = 0; i < n; i++) {
    const k = read(r, o, depth + 1);
    if (typeof k !== 'string' && typeof k !== 'number') throw new MsgPackError('Map key is not a string or number (use mapAsMap).');
    const key = String(k);
    const value = read(r, o, depth + 1);
    if (key === '__proto__') Object.defineProperty(obj, key, { value, enumerable: true, writable: true, configurable: true });
    else obj[key] = value;
  }
  return obj;
}

function ext(r: Reader, n: number): MsgPackExt {
  const t = r.u8();
  return new MsgPackExt(t > 0x7f ? t - 0x100 : t, r.bytes(n));
}

/**
 * Offset just past the MessagePack value at `pos`, or -1 when the type is unknown or the data is
 * truncated. Like C# `MessageEnvelopeShape.SkipValue`, but complete (bin32, map32 and ext are
 * supported) and never throws.
 */
export function skipMsgPackValue(data: Uint8Array, pos: number): number {
  try {
    const r = new Reader(data);
    r.pos = pos;
    skip(r, 0);
    return r.pos;
  } catch {
    return -1;
  }
}

function skip(r: Reader, depth: number): void {
  if (depth > 100) throw new MsgPackError('too deep');
  const b = r.u8();
  if (b <= 0x7f || b >= 0xe0) return;
  const adv = (n: number) => {
    r.need(n);
    r.pos += n;
  };
  if ((b & 0xe0) === 0xa0) return adv(b & 0x1f);
  if ((b & 0xf0) === 0x90) {
    for (let i = 0; i < (b & 0x0f); i++) skip(r, depth + 1);
    return;
  }
  if ((b & 0xf0) === 0x80) {
    for (let i = 0; i < (b & 0x0f) * 2; i++) skip(r, depth + 1);
    return;
  }
  switch (b) {
    case 0xc0:
    case 0xc2:
    case 0xc3:
      return;
    case 0xcc:
    case 0xd0:
      return adv(1);
    case 0xcd:
    case 0xd1:
      return adv(2);
    case 0xce:
    case 0xd2:
    case 0xca:
      return adv(4);
    case 0xcf:
    case 0xd3:
    case 0xcb:
      return adv(8);
    case 0xd9:
    case 0xc4:
      return adv(r.u8());
    case 0xda:
    case 0xc5:
      return adv(r.u16());
    case 0xdb:
    case 0xc6:
      return adv(r.u32());
    case 0xdc: {
      const n = r.u16();
      for (let i = 0; i < n; i++) skip(r, depth + 1);
      return;
    }
    case 0xdd: {
      const n = r.u32();
      for (let i = 0; i < n; i++) skip(r, depth + 1);
      return;
    }
    case 0xde: {
      const n = r.u16() * 2;
      for (let i = 0; i < n; i++) skip(r, depth + 1);
      return;
    }
    case 0xdf: {
      const n = r.u32() * 2;
      for (let i = 0; i < n; i++) skip(r, depth + 1);
      return;
    }
    case 0xd4:
      return adv(2);
    case 0xd5:
      return adv(3);
    case 0xd6:
      return adv(5);
    case 0xd7:
      return adv(9);
    case 0xd8:
      return adv(17);
    case 0xc7:
      return adv(r.u8() + 1);
    case 0xc8:
      return adv(r.u16() + 1);
    case 0xc9:
      return adv(r.u32() + 1);
    default:
      throw new MsgPackError('bad type');
  }
}
