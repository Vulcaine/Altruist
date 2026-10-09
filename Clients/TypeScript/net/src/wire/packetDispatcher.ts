/**
 * Routes server frames to handlers by message code: the TypeScript twin of C#
 * `ClientPacketDispatcher`. A game registers each server packet once — its code, a decoder from
 * the MessagePack message to a typed object (hand-written, or a snapshot schema's
 * `schema.packet(name)`), and handlers — and feeds every received frame to {@link dispatch}.
 *
 * Use it instead of a `switch (code)` over decoded frames. {@link AltruistSocket} owns one
 * (`socket.packets`) and dispatches every frame it receives; construct your own for tests or for
 * frames from another source (replays).
 *
 * Behaviour, as in C#: frames with code 0, unregistered codes, malformed frames and decoder
 * failures are dropped and reported to `onDrop` (never thrown); a throwing handler does not stop
 * the others (its error goes to `onError`). The frame is decoded once and each distinct decoder
 * runs once per frame. Synchronous, on the caller's stack.
 */
import { decodeEnvelope, peekMessageCode, type PacketHeader } from './envelope.ts';
import type { BytesLike, DecodeOptions } from './msgpack.ts';

/** Turns a decoded MessagePack message (usually a `[Key]` array) into a typed packet. Throw to reject. */
export type PacketDecode<T> = (message: ArrayLike<unknown>, code: number) => T;

/** What a handler learns about the frame besides the packet. */
export interface PacketContext {
  code: number;
  header: PacketHeader | null;
  /**
   * Milliseconds a dev network conditioner held this frame plus the latest outbound one
   * ({@link NetConditioner}); 0 without one. Subtract it from measured round trips to see the real network.
   */
  simulatedMs: number;
}

/** A server packet definition: its code and decoder. Create with {@link serverPacket}. */
export interface ServerPacket<T> {
  readonly code: number;
  readonly decode: PacketDecode<T>;
}

/**
 * Declares a server packet once (code + decoder) so registration and tests share it.
 *
 * @example
 * ```ts
 * const Welcome = serverPacket(2001, (m) => ({ playerId: Number(m[1]), roomId: String(m[5]) }));
 * socket.packets.on(Welcome, (w) => startMatch(w.roomId));
 * ```
 */
export function serverPacket<T>(code: number, decode: PacketDecode<T>): ServerPacket<T> {
  if (!Number.isInteger(code) || code <= 0) throw new RangeError(`Message code must be a positive integer: ${code}.`);
  return { code, decode };
}

/** Why a frame was not handled (see {@link PacketDispatcherOptions.onDrop}). */
export type DropReason = 'empty' | 'unrecognised' | 'unhandled' | 'malformed' | 'decode-failed';

/** Options of {@link PacketDispatcher}. */
export interface PacketDispatcherOptions {
  /** A frame was dropped (default: ignored). `code` is 0 when unknown. */
  onDrop?: (reason: DropReason, code: number, error?: unknown) => void;
  /** A handler threw (default: rethrown in a microtask). */
  onError?: (error: unknown, code: number) => void;
  /** MessagePack decode options for frames (e.g. `int64: 'bigint'`). */
  decode?: DecodeOptions;
}

interface Entry {
  decode: PacketDecode<unknown> | null;
  handler: (packet: unknown, ctx: PacketContext) => void;
}

/** See the module comment. */
export class PacketDispatcher {
  private readonly handlers = new Map<number, Entry[]>();
  private readonly any = new Set<(code: number, message: unknown, ctx: PacketContext) => void>();

  private readonly options: PacketDispatcherOptions;

  /** Creates an empty dispatcher. */
  constructor(options: PacketDispatcherOptions = {}) {
    this.options = options;
  }

  /**
   * Registers `handler` for `packet` (a {@link serverPacket}); it receives the decoded packet.
   * Returns the unregister function. Several handlers per code are fine; they run in
   * registration order.
   */
  on<T>(packet: ServerPacket<T>, handler: (packet: T, ctx: PacketContext) => void): () => void;
  /**
   * Registers `handler` for a raw code with an inline decoder, or without one (the handler then
   * gets the undecoded MessagePack message, whatever its shape).
   */
  on<T = unknown>(code: number, handler: (packet: T, ctx: PacketContext) => void, decode?: PacketDecode<T>): () => void;
  on<T>(packetOrCode: ServerPacket<T> | number, handler: (packet: T, ctx: PacketContext) => void, decode?: PacketDecode<T>): () => void {
    const code = typeof packetOrCode === 'number' ? packetOrCode : packetOrCode.code;
    const dec = typeof packetOrCode === 'number' ? (decode ?? null) : packetOrCode.decode;
    const entry: Entry = { decode: dec as PacketDecode<unknown> | null, handler: handler as Entry['handler'] };
    const list = this.handlers.get(code) ?? [];
    this.handlers.set(code, [...list, entry]);
    return () => {
      const cur = this.handlers.get(code);
      if (!cur) return;
      const next = cur.filter((e) => e !== entry);
      if (next.length) this.handlers.set(code, next);
      else this.handlers.delete(code);
    };
  }

  /** Like {@link on}, removed after the first delivery. */
  once<T>(packet: ServerPacket<T>, handler: (packet: T, ctx: PacketContext) => void): () => void {
    const off = this.on(packet, (p, ctx) => {
      off();
      handler(p, ctx);
    });
    return off;
  }

  /**
   * Observes every well-formed frame (registered or not) with its raw message — logging, metrics,
   * a generic fallback. Runs before the code's handlers.
   */
  onAny(handler: (code: number, message: unknown, ctx: PacketContext) => void): () => void {
    this.any.add(handler);
    return () => this.any.delete(handler);
  }

  /** Number of handlers registered for `code` (C# `HandlerCountForMC`). */
  handlerCount(code: number): number {
    return this.handlers.get(code)?.length ?? 0;
  }

  /**
   * Dispatches one server frame. Returns true when at least one handler ran.
   * `simulatedMs`: see {@link PacketContext.simulatedMs}.
   */
  dispatch(frame: BytesLike, simulatedMs = 0): boolean {
    const bytes = frame;
    if ((bytes as ArrayBuffer).byteLength === 0) {
      this.drop('empty', 0);
      return false;
    }
    const code = peekMessageCode(bytes);
    if (code === 0) {
      this.drop('unrecognised', 0);
      return false;
    }
    const entries = this.handlers.get(code);
    if (!entries?.length && this.any.size === 0) {
      this.drop('unhandled', code);
      return false;
    }
    let env;
    try {
      env = decodeEnvelope(bytes, this.options.decode);
    } catch (error) {
      this.drop('malformed', code, error);
      return false;
    }
    const ctx: PacketContext = { code, header: env.header, simulatedMs };
    for (const l of [...this.any]) {
      try {
        l(code, env.message, ctx);
      } catch (error) {
        this.fail(error, code);
      }
    }
    if (!entries?.length) {
      this.drop('unhandled', code);
      return false;
    }
    const decoded = new Map<PacketDecode<unknown>, { ok: boolean; value: unknown }>();
    let ran = false;
    for (const e of entries) {
      let packet: unknown = env.message;
      if (e.decode) {
        let r = decoded.get(e.decode);
        if (!r) {
          try {
            if (!Array.isArray(env.message)) throw new TypeError('Packet message is not an array.');
            r = { ok: true, value: e.decode(env.message, code) };
          } catch (error) {
            r = { ok: false, value: undefined };
            this.drop('decode-failed', code, error);
          }
          decoded.set(e.decode, r);
        }
        if (!r.ok) continue;
        packet = r.value;
      }
      try {
        ran = true;
        e.handler(packet, ctx);
      } catch (error) {
        this.fail(error, code);
      }
    }
    return ran;
  }

  /** Removes every handler. */
  clear(): void {
    this.handlers.clear();
    this.any.clear();
  }

  private drop(reason: DropReason, code: number, error?: unknown): void {
    this.options.onDrop?.(reason, code, error);
  }

  private fail(error: unknown, code: number): void {
    if (this.options.onError) this.options.onError(error, code);
    else
      queueMicrotask(() => {
        throw error;
      });
  }
}
