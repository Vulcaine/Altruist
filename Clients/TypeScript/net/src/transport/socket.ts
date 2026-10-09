/**
 * `AltruistSocket` — one logical WebSocket connection to an Altruist server: the browser twin of
 * C# `AltruistClientRouter` + `AltruistWebSocketClient`, with what a game client needs on top:
 *
 * - **Connect** to a URL from a provider called for every (re)connect (so single-use connection
 *   tickets work: see {@link ticketUrl}), with a connect timeout and optional direct-host
 *   {@link EndpointFailover}.
 * - **Hello**: the frames a fresh socket sends first (`hello` option; e.g. join / queue / rejoin),
 *   told whether this is the first connect, a reconnect or a redirect.
 * - **Reconnect** (`autoReconnect`, off by default; turn it on while in a match): an unexpected
 *   close with a retryable code (1001, 1006, 1011, 1012, 1013, 1014 by default) retries with
 *   {@link backoffDelay} until `graceMs` after the first drop; any other close code is final, and
 *   so is a URL provider error the `isFatalError` policy rejects (default: HTTP 401 / 403, i.e. the
 *   session ended). After a reconnect the state stays `reconnecting` until the game confirms it is
 *   back ({@link confirmRejoin}, or a frame with a `rejoinConfirmCodes` code); without that within
 *   `rejoinWaitMs` the state becomes `rejoinFailed` (retries continue on the next drop).
 * - **Heartbeat**: Altruist closes connections that send nothing for its idle timeout; with the
 *   `heartbeat` option a ping frame goes out after `intervalMs` without any send.
 * - **Fleet handoff**: {@link redirect} (C# `RoomRedirect`) moves the connection to another server
 *   transparently — same listeners, a fresh ticket, a `redirect` hello — and every later reconnect
 *   asks for that server; {@link forgetNode} after a "server draining" notice.
 * - **Routing**: every binary frame goes to {@link packets} (a {@link PacketDispatcher}) and the
 *   `frame` event.
 * - **Dev conditioning**: frames in both directions go through a {@link NetConditioner} when given.
 *
 * Single-threaded (browser event loop): events fire from WebSocket callbacks and timers.
 * Use one instance per logical connection (menu / queue / match); create a new one after a final close.
 *
 * @example
 * ```ts
 * const socket = new AltruistSocket({
 *   url: ticketUrl({ base: 'wss://play.example.com/game', fetchTicket: () => api.post('/game/ticket') }),
 *   hello: ({ reason }) => (reason === 'connect' ? packetFrame('queue', 1007, 'duel') : packetFrame('rejoin', 1010, roomId)),
 *   heartbeat: { frame: () => packetFrame('ping', 1003, performance.now()) },
 *   rejoinConfirmCodes: [Welcome.code],
 * });
 * socket.packets.on(Welcome, (w) => { roomId = w.roomId; socket.autoReconnect = true; });
 * socket.on('close', ({ reason }) => showError(reason));
 * await socket.connect();
 * ```
 */
import { DelayLine, type NetConditioner } from '../diagnostics/conditioner.ts';
import { backoffDelay, type BackoffOptions } from '../util/backoff.ts';
import { Emitter } from '../util/emitter.ts';
import { clientFrame, peekMessageCode as peek } from '../wire/envelope.ts';
import { asBytes, type BytesLike } from '../wire/msgpack.ts';
import { PacketDispatcher, type PacketDispatcherOptions } from '../wire/packetDispatcher.ts';
import { withNode, type EndpointFailover, type RoomRedirect } from './endpoint.ts';
import type { ticketUrl } from './endpoint.ts';

/** The subset of the browser `WebSocket` the socket uses (inject a fake for tests or Node). */
export interface WebSocketLike {
  binaryType: string;
  readonly readyState: number;
  send(data: Uint8Array<ArrayBuffer>): void;
  close(code?: number, reason?: string): void;
  onopen: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
  onclose: ((ev: { code: number; reason?: string; wasClean?: boolean }) => void) | null;
}

/** Lifecycle of an {@link AltruistSocket}. `rejoined` is a one-tick state before `open` again. */
export type SocketState = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'rejoined' | 'rejoinFailed' | 'closed';

/** Why the frames of a {@link AltruistSocketOptions.hello} are sent. */
export interface HelloContext {
  /** `connect`: the first socket; `reconnect`: after a drop; `redirect`: moving to another server. */
  reason: 'connect' | 'reconnect' | 'redirect';
  /** The fleet server this connection belongs to (after a redirect), else null. */
  node: RoomRedirect | null;
}

/** Why the connection ended for good. */
export type SocketCloseReason =
  /** The server (or network) closed with a code that is not retried, or autoReconnect was off. */
  | 'closed'
  /** Reconnects ran past `graceMs`. */
  | 'grace-expired'
  /** The URL provider failed with a fatal error (default: 401 / 403 — the session ended). */
  | 'unauthorized'
  /** The first connect failed (cannot reach the server, timeout). */
  | 'unreachable';

/** Payload of the `close` event. */
export interface SocketClose {
  reason: SocketCloseReason;
  /** WebSocket close code of the last socket (0 when none closed). */
  code: number;
  /** The error behind `unauthorized` / `unreachable`. */
  error?: unknown;
}

/** Events of {@link AltruistSocket}. */
export type SocketEvents = {
  /** Every state change. */
  state: [next: SocketState, previous: SocketState];
  /** A socket opened and its hello was sent. */
  open: [ctx: HelloContext];
  /** A binary frame arrived (after the conditioner). `simulatedMs`: see `PacketContext.simulatedMs`. */
  frame: [bytes: Uint8Array, simulatedMs: number];
  /** A text frame arrived (JSON codec servers); not dispatched. */
  text: [data: string];
  /** One underlying socket closed unexpectedly (a reconnect may follow). */
  drop: [code: number];
  /** The connection ended for good (not emitted by {@link AltruistSocket.close}). */
  close: [close: SocketClose];
  /** The connection moved to another server. */
  redirect: [target: RoomRedirect];
};

/** Options of {@link AltruistSocket}. All times in ms. */
export interface AltruistSocketOptions {
  /** Socket URL, or a provider called for every connect (see {@link ticketUrl}). */
  url: string | (() => string | Promise<string>);
  /** Frames to send first on every socket (default none). */
  hello?: (ctx: HelloContext) => Uint8Array<ArrayBuffer> | readonly Uint8Array<ArrayBuffer>[] | null | undefined;
  /** Creates the WebSocket (default `new WebSocket(url)`). */
  createWebSocket?: (url: string) => WebSocketLike;
  /** Time for a socket to open (default 8000; a failover primary uses its own `connectTimeoutMs`). */
  connectTimeoutMs?: number;
  /** Direct-host failover: a primary that does not open is marked down and the connect retried once. */
  failover?: EndpointFailover | null;
  /** Reconnect on unexpected drops (default false; settable later via {@link AltruistSocket.autoReconnect}). */
  autoReconnect?: boolean;
  /** Reconnect window from the first drop (default 28000: under Altruist's 30 s room reconnect grace). */
  graceMs?: number;
  /** Close codes that are retried (default 1001, 1006, 1011, 1012, 1013, 1014). */
  retryCloseCodes?: Iterable<number>;
  /** Delay between reconnect attempts (default 400 ms doubling to 4 s). */
  backoff?: BackoffOptions;
  /** Time a reconnected socket waits for {@link AltruistSocket.confirmRejoin} (default 6000). */
  rejoinWaitMs?: number;
  /** Frame codes that confirm a rejoin by arriving (e.g. your welcome / lobby state packets). */
  rejoinConfirmCodes?: Iterable<number>;
  /** URL provider errors that end the connection (default: `status` 401 or 403). */
  isFatalError?: (error: unknown) => boolean;
  /** Keep-alive: `frame` is sent after `intervalMs` (default 10000) without any send. */
  heartbeat?: { frame: () => Uint8Array<ArrayBuffer>; intervalMs?: number } | null;
  /** Dev network conditioner (both directions). */
  conditioner?: NetConditioner | null;
  /** Options of the built-in {@link PacketDispatcher} (ignored when `dispatcher` is given). */
  dispatcherOptions?: PacketDispatcherOptions;
  /** Use this dispatcher instead of a new one. */
  dispatcher?: PacketDispatcher;
  /** Monotonic clock (default `performance.now()`). */
  now?: () => number;
}

const OPEN = 1;
const DEFAULT_RETRY_CODES = [1001, 1006, 1011, 1012, 1013, 1014];

/** Default {@link AltruistSocketOptions.isFatalError}: an error with `status` 401 or 403. */
export function isAuthError(error: unknown): boolean {
  const s = (error as { status?: unknown } | null)?.status;
  return s === 401 || s === 403;
}

/** Error of a socket that could not open. */
export class SocketConnectError extends Error {
  /** Creates the error. */
  /** `timeout`: did not open in time; `unreachable`: failed to open; `closed`: the socket was closed meanwhile. */
  readonly kind: 'timeout' | 'unreachable' | 'closed';

  constructor(message: string, kind: 'timeout' | 'unreachable' | 'closed') {
    super(message);
    this.kind = kind;
    this.name = 'SocketConnectError';
  }
}

/** See the module comment. */
export class AltruistSocket {
  /** Routes received frames by message code. */
  readonly packets: PacketDispatcher;
  private readonly events = new Emitter<SocketEvents>();
  private readonly o: AltruistSocketOptions;
  private readonly retryCodes: Set<number>;
  private readonly confirmCodes: Set<number>;
  private ws: WebSocketLike | null = null;
  private _state: SocketState = 'idle';
  private finished = false;
  private dropAt = 0;
  private attempt = 0;
  private lastCode = 0;
  private lastSend = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private rejoinTimer: ReturnType<typeof setTimeout> | null = null;
  private heartbeat: ReturnType<typeof setInterval> | null = null;
  private _node: RoomRedirect | null = null;
  private _url: string | null = null;
  private redirecting = false;
  private connectP: Promise<void> | null = null;
  private readonly up: DelayLine<{ ws: WebSocketLike; data: Uint8Array<ArrayBuffer> }> | null;
  private readonly down: DelayLine<{ ws: WebSocketLike; data: Uint8Array | null; close?: () => void }> | null;
  /** Reconnect on unexpected drops (see the module comment). */
  autoReconnect: boolean;

  /** Creates the socket; nothing connects until {@link connect}. */
  constructor(options: AltruistSocketOptions) {
    this.o = options;
    this.autoReconnect = options.autoReconnect ?? false;
    this.retryCodes = new Set(options.retryCloseCodes ?? DEFAULT_RETRY_CODES);
    this.confirmCodes = new Set(options.rejoinConfirmCodes ?? []);
    this.packets = options.dispatcher ?? new PacketDispatcher(options.dispatcherOptions);
    const c = options.conditioner;
    this.up = c
      ? c.line<{ ws: WebSocketLike; data: Uint8Array<ArrayBuffer> }>(({ ws, data }) => {
          if (ws.readyState === OPEN) ws.send(data);
        })
      : null;
    this.down = c
      ? c.line<{ ws: WebSocketLike; data: Uint8Array | null; close?: () => void }>((m, held) => {
          if (m.close) return m.close();
          if (this.ws === m.ws && m.data) this.receive(m.data, held + (this.up?.lastHeld ?? 0));
        })
      : null;
  }

  /** Current lifecycle state. */
  get state(): SocketState {
    return this._state;
  }

  /** True while the underlying socket is open (frames can be sent). */
  get isOpen(): boolean {
    return this.ws?.readyState === OPEN;
  }

  /** The fleet server this connection belongs to (set by {@link redirect}), or null. */
  get node(): RoomRedirect | null {
    return this._node;
  }

  /**
   * The URL the latest socket opened on (with its ticket and fleet node), or null before the first
   * open. Use it to label the network path, e.g. `failover.isPrimary(socket.url)` for a ping badge.
   */
  get url(): string | null {
    return this._url;
  }

  /** Subscribes to an event (see {@link SocketEvents}); returns the unsubscribe function. */
  on<K extends keyof SocketEvents>(event: K, listener: (...args: SocketEvents[K]) => void): () => void {
    return this.events.on(event, listener);
  }

  /**
   * Opens the first socket and sends the hello. Resolves once open; rejects with the URL
   * provider's error or a {@link SocketConnectError} (the socket is then `closed` and emits
   * `close` with reason `unreachable` / `unauthorized`). Calling it again returns the same promise.
   */
  connect(): Promise<void> {
    if (this.connectP) return this.connectP;
    this.setState('connecting');
    this.connectP = this.open('connect').then(
      () => this.setState('open'),
      (error: unknown) => {
        this.finish(this.fatal(error) ? 'unauthorized' : 'unreachable', error);
        throw error;
      },
    );
    return this.connectP;
  }

  /** Sends a frame when open (through the conditioner). Returns false when not open (dropped). */
  send(frame: Uint8Array<ArrayBuffer>): boolean {
    const ws = this.ws;
    if (!ws || ws.readyState !== OPEN) return false;
    if (this.up) this.up.push({ ws, data: frame });
    else ws.send(frame);
    this.lastSend = this.now();
    return true;
  }

  /** `send(clientFrame(event, payload))`: a gate name plus a MessagePack payload. */
  sendEvent(event: string, payload: unknown): boolean {
    return this.send(clientFrame(event, payload));
  }

  /**
   * The game confirmed it is back after a reconnect (its welcome / lobby state arrived):
   * `reconnecting` / `rejoinFailed` → `rejoined` → `open`. No-op in other states.
   */
  confirmRejoin(): void {
    if (this._state !== 'reconnecting' && this._state !== 'rejoinFailed') return;
    this.clearTimer('rejoin');
    this.setState('rejoined');
    this.setState('open');
  }

  /**
   * Moves this connection to another fleet server (decode your game's redirect packet into a
   * {@link RoomRedirect}): a new socket opens there with a `redirect` hello (fresh URL / ticket),
   * then the old one closes. Every later reconnect also goes there. On failure: a fatal error ends
   * the connection; otherwise it reconnects when `autoReconnect` is on, else ends (`closed`).
   */
  redirect(target: RoomRedirect): void {
    if (this.finished) return;
    this._node = target;
    this.redirecting = true;
    const old = this.ws;
    this.events.emit('redirect', target);
    this.open('redirect').then(
      () => {
        this.redirecting = false;
        if (old && old !== this.ws) old.close(1000);
      },
      (error: unknown) => {
        this.redirecting = false;
        if (this.fatal(error)) this.finish('unauthorized', error);
        else if (this.autoReconnect) this.beginReconnect();
        else this.finish('closed', error);
      },
    );
  }

  /** The server is draining: later reconnects go wherever the load balancer sends them. */
  forgetNode(): void {
    this._node = null;
  }

  /**
   * Closes for good with `code` (default 1000), without a `close` event: frames already sent
   * through the conditioner are flushed, queued inbound ones are discarded. Idempotent.
   */
  close(code = 1000): void {
    if (this.finished) return;
    this.finished = true;
    this.clearTimers();
    this.up?.flush();
    this.down?.clear();
    const ws = this.ws;
    this.ws = null;
    try {
      ws?.close(code);
    } catch {
      /* already closing */
    }
    this.setState('closed');
  }

  // ---------------------------------------------------------------- internals

  private now(): number {
    return (this.o.now ?? (() => performance.now()))();
  }

  private fatal(error: unknown): boolean {
    return (this.o.isFatalError ?? isAuthError)(error);
  }

  private setState(next: SocketState): void {
    if (this._state === next) return;
    const prev = this._state;
    this._state = next;
    this.events.emit('state', next, prev);
  }

  private async resolveUrl(): Promise<string> {
    const u = this.o.url;
    return withNode(typeof u === 'string' ? u : await u(), this._node);
  }

  /** Opens a socket (with one failover retry) and sends the hello. */
  private async open(reason: HelloContext['reason']): Promise<void> {
    const url = await this.resolveUrl();
    try {
      return await this.openAt(url, reason);
    } catch (err) {
      const f = this.o.failover;
      if (this.finished || !f || !f.isPrimary(url) || !f.markDown()) throw err;
      return this.openAt(await this.resolveUrl(), reason);
    }
  }

  private openAt(url: string, reason: HelloContext['reason']): Promise<void> {
    if (this.finished) return Promise.reject(new SocketConnectError('Socket closed.', 'closed'));
    const f = this.o.failover;
    const timeoutMs = f && f.isPrimary(url) ? f.connectTimeoutMs : (this.o.connectTimeoutMs ?? 8000);
    return new Promise<void>((resolve, reject) => {
      const create = this.o.createWebSocket ?? ((u: string) => new (globalThis as unknown as { WebSocket: new (u: string) => WebSocketLike }).WebSocket(u));
      let ws: WebSocketLike;
      try {
        ws = create(url);
      } catch (e) {
        reject(e);
        return;
      }
      ws.binaryType = 'arraybuffer';
      this.ws = ws;
      let opened = false;
      let failed = false;
      const fail = (err: SocketConnectError) => {
        if (opened || failed) return;
        failed = true;
        clearTimeout(timer);
        reject(err);
      };
      const timer = setTimeout(() => {
        fail(new SocketConnectError('Timed out connecting to the server.', 'timeout'));
        try {
          ws.close();
        } catch {
          /* ignore */
        }
      }, timeoutMs);
      ws.onopen = () => {
        if (failed) return;
        clearTimeout(timer);
        opened = true;
        this._url = url;
        this.lastSend = this.now();
        const ctx: HelloContext = { reason, node: this._node };
        const hello = this.o.hello?.(ctx);
        const frames = hello == null ? [] : hello instanceof Uint8Array ? [hello] : hello;
        for (const frame of frames) {
          if (this.up) this.up.push({ ws, data: frame });
          else ws.send(frame);
        }
        this.startHeartbeat();
        this.events.emit('open', ctx);
        resolve();
      };
      ws.onerror = () => fail(new SocketConnectError('Cannot reach the server.', 'unreachable'));
      ws.onmessage = (e) => {
        if (this.ws !== ws) return;
        const data = e.data;
        if (typeof data === 'string') {
          this.events.emit('text', data);
          return;
        }
        if (!(data instanceof ArrayBuffer) && !ArrayBuffer.isView(data)) return;
        const bytes = asBytes(data as BytesLike);
        if (this.down) this.down.push({ ws, data: bytes });
        else this.receive(bytes, 0);
      };
      ws.onclose = (e) => {
        if (!opened) return fail(new SocketConnectError('Cannot reach the server.', 'unreachable'));
        const handle = () => {
          if (this.ws !== ws || this.finished || this.redirecting) return;
          this.onDrop(e.code);
        };
        // Behind the conditioner the close arrives after the frames sent before it.
        if (this.down) this.down.push({ ws, data: null, close: handle });
        else handle();
      };
    });
  }

  private receive(bytes: Uint8Array, simulatedMs: number): void {
    this.events.emit('frame', bytes, simulatedMs);
    this.packets.dispatch(bytes, simulatedMs);
    if (this.confirmCodes.size && (this._state === 'reconnecting' || this._state === 'rejoinFailed')) {
      if (this.confirmCodes.has(peek(bytes))) this.confirmRejoin();
    }
  }

  private onDrop(code: number): void {
    this.lastCode = code;
    this.events.emit('drop', code);
    const retrying = this._state === 'reconnecting' || this._state === 'rejoinFailed';
    if (this.autoReconnect && (retrying || this.retryCodes.has(code))) {
      if (!retrying) this.beginReconnect();
      else this.scheduleRetry();
      return;
    }
    this.finish('closed');
  }

  private beginReconnect(): void {
    // The grace counts from the first drop, not from a failed rejoin socket.
    this.dropAt = this.now();
    this.attempt = 0;
    this.setState('reconnecting');
    this.scheduleRetry();
  }

  private scheduleRetry(): void {
    if (this.finished || this.retryTimer) return;
    if (this.now() - this.dropAt > (this.o.graceMs ?? 28_000)) {
      this.finish('grace-expired');
      return;
    }
    const delay = backoffDelay(this.attempt++, this.o.backoff);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.open('reconnect').then(
        () => {
          this.clearTimer('rejoin');
          if (this._state === 'rejoinFailed') this.setState('reconnecting');
          this.rejoinTimer = setTimeout(() => {
            this.rejoinTimer = null;
            if (this._state === 'reconnecting') this.setState('rejoinFailed');
          }, this.o.rejoinWaitMs ?? 6000);
        },
        (error: unknown) => {
          if (this.fatal(error)) this.finish('unauthorized', error);
          else this.scheduleRetry();
        },
      );
    }, delay);
  }

  private startHeartbeat(): void {
    const hb = this.o.heartbeat;
    if (!hb || this.heartbeat || this.finished) return;
    const every = hb.intervalMs ?? 10_000;
    this.heartbeat = setInterval(() => {
      if (this.now() - this.lastSend >= every) this.send(hb.frame());
    }, every / 4);
  }

  private finish(reason: SocketCloseReason, error?: unknown): void {
    if (this.finished) return;
    this.finished = true;
    this.clearTimers();
    this.down?.clear();
    const ws = this.ws;
    this.ws = null;
    try {
      if (ws && ws.readyState <= OPEN) ws.close(1000);
    } catch {
      /* ignore */
    }
    this.setState('closed');
    this.events.emit('close', error === undefined ? { reason, code: this.lastCode } : { reason, code: this.lastCode, error });
  }

  private clearTimer(which: 'rejoin' | 'retry'): void {
    if (which === 'rejoin' && this.rejoinTimer) {
      clearTimeout(this.rejoinTimer);
      this.rejoinTimer = null;
    }
    if (which === 'retry' && this.retryTimer) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
  }

  private clearTimers(): void {
    this.clearTimer('rejoin');
    this.clearTimer('retry');
    if (this.heartbeat) clearInterval(this.heartbeat);
    this.heartbeat = null;
  }
}

