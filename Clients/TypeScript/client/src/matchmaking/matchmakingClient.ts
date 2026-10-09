/**
 * `MatchmakingClient` — the client side of Altruist's match hosting (`MatchmakingModule`,
 * `LobbyModule`, `RoomHost` rejoin and fleet redirects) as a phase machine over one
 * {@link AltruistSocket}:
 *
 *     idle ──queue()──▶ queueing ──welcome──▶ match ──finish()──▶ post ──reset()──▶ idle
 *     idle ──rejoin()─▶ rejoining ─welcome──▶ match
 *     idle ──lobby()──▶ lobby ────welcome──▶ match
 *
 * The game supplies its packets through a {@link MatchmakingProtocol} (Altruist does not fix the
 * packet layouts: `IMatchmakingGame` / `ILobbyGame` / `IRoomGame` build them), and the client does
 * the rest: opens a ticketed socket with the right hello (queue / lobby on the first connect,
 * rejoin on reconnects and redirects), turns on auto-reconnect once a match starts, confirms
 * rejoins on the welcome / lobby state, follows `RoomRedirect`s, forgets the fleet node on
 * "server draining", answers "nothing to rejoin" with `concluded`, closes the socket on cancel
 * (after a short grace so the cancel reaches the server) and reports everything as typed events.
 * UI (overlays, prompts, toasts) stays in the game.
 *
 * Single-threaded (event loop). One instance per app; it creates a new socket for every queue /
 * rejoin / lobby session.
 */
import { AltruistSocket, type AltruistSocketOptions, type RoomRedirect, type SocketClose } from '../../../net/src/transport/index.ts';
import { Emitter } from '../../../net/src/util/emitter.ts';
import type { ServerPacket } from '../../../net/src/wire/packetDispatcher.ts';
import { lobbyRejectReasonName, type LobbyRejectReasonName, type QueueStatusInfo, type RoomNotice } from './enums.ts';

/** Phase of the {@link MatchmakingClient}. */
export type MatchPhase = 'idle' | 'queueing' | 'rejoining' | 'lobby' | 'match' | 'post';

/** What every welcome packet must carry: the room (match) id, used for rejoins. */
export interface WelcomeLike {
  roomId: string;
}

/**
 * The game's packets for the flow. Outbound entries build complete client frames (use
 * `packetFrame(event, code, ...)` of `@altruist/net`); inbound entries are `serverPacket`s.
 */
export interface MatchmakingProtocol<TStatus extends QueueStatusInfo<string>, TWelcome extends WelcomeLike, TLobby = unknown, TResult = unknown> {
  /** Joins a playlist's queue (the first frame of a queue socket). */
  queue(playlist: string): Uint8Array<ArrayBuffer>;
  /** Leaves the queue. */
  cancelQueue(): Uint8Array<ArrayBuffer>;
  /** Asks to go back to `roomId` (or to whatever the server holds for this account when null): sent on rejoin sockets, reconnects and redirects. */
  rejoin(roomId: string | null): Uint8Array<ArrayBuffer>;
  /** Leaves a running match on purpose (the server frees the seat at once instead of holding it for the grace). */
  leave?(): Uint8Array<ArrayBuffer>;
  /** Queue status (C# `IMatchmakingGame.QueueStatus`). */
  queueStatus: ServerPacket<TStatus>;
  /** "You are in the room" (C# `IRoomGame.Welcome`), on join and on every rejoin. */
  welcome: ServerPacket<TWelcome>;
  /** Lobby state (C# `ILobbyGame.LobbyState`). */
  lobbyState?: ServerPacket<TLobby>;
  /** A refused lobby request (C# `LobbyRejectReason`); decode `reason` to a name or the game's own string. */
  lobbyReject?: ServerPacket<{ reason: LobbyRejectReasonName | string }>;
  /** Room notices (C# `RoomNoticeKind`). */
  notice?: ServerPacket<RoomNotice>;
  /** Fleet redirect (C# `IRoomGame.Redirect(RoomRedirect)`). */
  redirect?: ServerPacket<RoomRedirect>;
  /** The server is going away (C# `IRoomGame.ServerDraining`). */
  serverDraining?: ServerPacket<unknown>;
  /** Match result (game-defined), delivered as the `result` event. */
  result?: ServerPacket<TResult>;
  /**
   * The answer to a rejoin when there is nothing to go back to (C# `IRoomGame.NothingToRejoin`):
   * either its own packet, or a predicate over queue statuses (e.g. `s => s.state === 'concluded'`).
   */
  nothingToRejoin?: ServerPacket<unknown> | ((status: TStatus) => boolean);
}

/** Why the flow stopped with an error. */
export type MatchErrorKind =
  /** The socket could not be opened (unreachable, timeout, ticket refused). */
  | 'connect'
  /** The socket closed while queueing / rejoining / in a lobby. */
  | 'lost'
  /** The connection to a running match ended for good (reconnects gave up). */
  | 'disconnected'
  /** A reconnected socket never got a welcome. */
  | 'rejoin-failed';

/** Events of {@link MatchmakingClient}. */
export type MatchmakingEvents<TStatus, TWelcome, TLobby, TResult> = {
  phase: [next: MatchPhase, previous: MatchPhase];
  /** Every queue status (also game-specific states such as bans: handle them here). */
  status: [status: TStatus];
  /** A queue status said `found`. */
  found: [status: TStatus];
  /** In the match: first entry (`rejoined` false) or back after a rejoin / reconnect (`rejoined` true). */
  match: [welcome: TWelcome, rejoined: boolean];
  lobby: [state: TLobby];
  lobbyRejected: [reason: LobbyRejectReasonName | string];
  notice: [notice: RoomNotice];
  /** The match is over for us (nothing to rejoin): forget `roomId` (it was ended or abandoned). */
  concluded: [roomId: string | null];
  /** The server ended the queue entry on its own (state `idle`, e.g. replaced by another window). */
  stopped: [];
  /** The server is draining (new work goes elsewhere). */
  draining: [];
  /** The match result arrived. */
  result: [result: TResult];
  /** No welcome within `rejoinWaitMs` of a rejoin socket: check the account's status (still in the match?). */
  rejoinTimeout: [roomId: string | null];
  /** The flow stopped (the phase is back to `idle`). */
  error: [kind: MatchErrorKind, detail: SocketClose | unknown];
};

/** Options of {@link MatchmakingClient}. */
export interface MatchmakingClientOptions<TStatus extends QueueStatusInfo<string>, TWelcome extends WelcomeLike, TLobby, TResult> {
  protocol: MatchmakingProtocol<TStatus, TWelcome, TLobby, TResult>;
  /** Socket settings (URL / ticket provider, heartbeat, reconnect, conditioner...); `hello` and `rejoinConfirmCodes` are set by the client. */
  socket: Omit<AltruistSocketOptions, 'hello' | 'rejoinConfirmCodes'>;
  /** Wait for a welcome after a rejoin socket opens before `rejoinTimeout` (ms, default 6000). */
  rejoinWaitMs?: number;
  /** Time the cancel / leave frame gets to reach the server before the socket closes (ms, default 250). */
  closeGraceMs?: number;
}

/**
 * See the module comment.
 *
 * @example
 * ```ts
 * const mm = new MatchmakingClient({
 *   protocol: {
 *     queue: (p) => packetFrame('queue', 1007, p),
 *     cancelQueue: () => packetFrame('cancelqueue', 1008),
 *     rejoin: (room) => packetFrame('rejoin', 1010, room ?? ''),
 *     leave: () => packetFrame('return', 1005),
 *     queueStatus: serverPacket(2007, (m) => ({ state: String(m[1]), playlist: String(m[2]), elapsed: Number(m[3]), playersFound: Number(m[4]), playersNeeded: Number(m[5]) })),
 *     welcome: serverPacket(2001, (m) => ({ roomId: String(m[5]) })),
 *     nothingToRejoin: (s) => s.state === 'concluded',
 *   },
 *   socket: { url: ticketUrl({ base: wsUrl, fetchTicket: () => api.connectionTicket('/game/ticket') }) },
 * });
 * mm.on('match', (welcome) => startMatchScene(mm.socket!, welcome));
 * mm.queue('ranked-duel');
 * ```
 */
export class MatchmakingClient<TStatus extends QueueStatusInfo<string>, TWelcome extends WelcomeLike, TLobby = unknown, TResult = unknown> {
  private readonly events = new Emitter<MatchmakingEvents<TStatus, TWelcome, TLobby, TResult>>();
  private readonly o: MatchmakingClientOptions<TStatus, TWelcome, TLobby, TResult>;
  private _phase: MatchPhase = 'idle';
  private _socket: AltruistSocket | null = null;
  private offs: (() => void)[] = [];
  private _playlist: string | null = null;
  private _roomId: string | null = null;
  private rejoinTarget: string | null = null;
  private rejoinTimer: ReturnType<typeof setTimeout> | null = null;
  private inLobby = false;

  /** Creates the client (idle; nothing connects). */
  constructor(options: MatchmakingClientOptions<TStatus, TWelcome, TLobby, TResult>) {
    this.o = options;
  }

  /** Current phase. */
  get phase(): MatchPhase {
    return this._phase;
  }

  /** True outside `idle`. */
  get busy(): boolean {
    return this._phase !== 'idle';
  }

  /** The live socket (send match inputs through it), or null. */
  get socket(): AltruistSocket | null {
    return this._socket;
  }

  /** The playlist being queued / played, or null. */
  get playlist(): string | null {
    return this._playlist;
  }

  /** The room of the latest welcome (or the one being rejoined), or null. */
  get roomId(): string | null {
    return this._roomId ?? this.rejoinTarget;
  }

  /** Subscribes to {@link MatchmakingEvents}. */
  on<K extends keyof MatchmakingEvents<TStatus, TWelcome, TLobby, TResult>>(
    event: K,
    listener: (...args: MatchmakingEvents<TStatus, TWelcome, TLobby, TResult>[K]) => void,
  ): () => void {
    return this.events.on(event, listener);
  }

  /**
   * Queues for `playlist` on a new socket. Ignored in a match / post phase and when already
   * queueing the same playlist; queueing another playlist cancels the current queue first.
   */
  queue(playlist: string): void {
    if (this._phase === 'match' || this._phase === 'post' || this._phase === 'lobby') return;
    if (this._phase === 'queueing' && this._playlist === playlist) return;
    if (this._phase !== 'idle') this.cancel();
    this._playlist = playlist;
    this.setPhase('queueing');
    this.open(() => this.o.protocol.queue(playlist));
  }

  /**
   * Goes back into a running match (e.g. after a page reload) on a new socket that says rejoin.
   * Emits `match` on the welcome, `concluded` when there is nothing to rejoin, `rejoinTimeout` when
   * the server says nothing within `rejoinWaitMs`. Only from `idle`.
   */
  rejoin(roomId: string | null, playlist: string | null = null): void {
    if (this._phase !== 'idle') return;
    this.rejoinTarget = roomId;
    this._playlist = playlist;
    this.setPhase('rejoining');
    this.open(() => this.o.protocol.rejoin(roomId));
    this.rejoinTimer = setTimeout(() => {
      this.rejoinTimer = null;
      if (this._phase === 'rejoining') this.events.emit('rejoinTimeout', roomId);
    }, this.o.rejoinWaitMs ?? 6000);
  }

  /**
   * Opens a lobby socket whose first frame is `hello` (your create / join-by-code packet). Lobby
   * states arrive as `lobby` events; a refusal as `lobbyRejected` (and back to idle when no lobby
   * was joined yet). Only from `idle`.
   */
  lobby(hello: Uint8Array<ArrayBuffer>): void {
    if (this._phase !== 'idle') return;
    this.inLobby = false;
    this.setPhase('lobby');
    this.open(() => hello);
  }

  /** Sends a frame on the current socket (lobby actions, in-match packets). False when not open. */
  send(frame: Uint8Array<ArrayBuffer>): boolean {
    return this._socket?.send(frame) ?? false;
  }

  /** Leaves the queue (or a pending rejoin): sends the cancel, closes after the grace, back to idle. */
  cancel(): void {
    if (this._phase !== 'queueing' && this._phase !== 'rejoining') return;
    const s = this.detach();
    s?.send(this.o.protocol.cancelQueue());
    this.closeLater(s);
    this.toIdle();
  }

  /**
   * Leaves a running match on purpose (sends `protocol.leave` when defined, so the server frees
   * the seat instead of holding it), closes the socket, back to idle. Also leaves a lobby.
   */
  leave(): void {
    if (this._phase !== 'match' && this._phase !== 'lobby') return;
    const s = this.detach();
    if (this._phase === 'match' && this.o.protocol.leave) s?.send(this.o.protocol.leave());
    this.closeLater(s);
    this.toIdle();
  }

  /** The match ended (your scene saw the final whistle): closes the socket, phase `post`. */
  finish(): void {
    if (this._phase !== 'match') return;
    this.detach()?.close();
    this.setPhase('post');
  }

  /** Back to idle from anywhere: closes the socket at once. */
  reset(): void {
    this.detach()?.close();
    this.toIdle();
  }

  // ---------------------------------------------------------------- internals

  private setPhase(next: MatchPhase): void {
    if (next === this._phase) return;
    const prev = this._phase;
    this._phase = next;
    this.events.emit('phase', next, prev);
  }

  private toIdle(): void {
    if (this.rejoinTimer) clearTimeout(this.rejoinTimer);
    this.rejoinTimer = null;
    this.rejoinTarget = null;
    this._roomId = null;
    this._playlist = null;
    this.inLobby = false;
    this.setPhase('idle');
  }

  private detach(): AltruistSocket | null {
    for (const off of this.offs) off();
    this.offs = [];
    const s = this._socket;
    this._socket = null;
    if (s) s.autoReconnect = false;
    return s;
  }

  private closeLater(s: AltruistSocket | null): void {
    if (!s) return;
    setTimeout(() => s.close(), this.o.closeGraceMs ?? 250);
  }

  private open(first: () => Uint8Array<ArrayBuffer>): void {
    const p = this.o.protocol;
    const confirm = [p.welcome.code, ...(p.lobbyState ? [p.lobbyState.code] : [])];
    const socket = new AltruistSocket({
      ...this.o.socket,
      autoReconnect: false,
      rejoinConfirmCodes: confirm,
      hello: ({ reason }) => (reason === 'connect' ? first() : p.rejoin(this._roomId ?? this.rejoinTarget)),
    });
    this._socket = socket;
    const mine = () => this._socket === socket;
    const pk = socket.packets;
    let opened = false;
    this.offs.push(
      socket.on('open', () => (opened = true)),
      pk.on(p.queueStatus, (s) => mine() && this.onStatus(s)),
      pk.on(p.welcome, (w) => mine() && this.onWelcome(w)),
      socket.on('close', (c) => mine() && this.onClose(c, opened)),
      socket.on('state', (s) => {
        if (!mine() || s !== 'rejoinFailed') return;
        this.detach()?.close();
        this.toIdle();
        this.events.emit('error', 'rejoin-failed', null);
      }),
    );
    if (p.lobbyState) {
      this.offs.push(
        pk.on(p.lobbyState, (l) => {
          if (!mine()) return;
          this.inLobby = true;
          if (this._phase !== 'match') this.setPhase('lobby');
          this.events.emit('lobby', l);
        }),
      );
    }
    if (p.lobbyReject) {
      this.offs.push(
        pk.on(p.lobbyReject, (r) => {
          if (!mine()) return;
          const reason = lobbyRejectReasonName(r.reason) ?? r.reason;
          this.events.emit('lobbyRejected', reason);
          if (this._phase === 'lobby' && !this.inLobby) this.reset();
        }),
      );
    }
    if (p.notice) this.offs.push(pk.on(p.notice, (n) => mine() && this.events.emit('notice', n)));
    if (p.redirect) this.offs.push(pk.on(p.redirect, (r) => mine() && socket.redirect(r)));
    if (p.serverDraining) {
      this.offs.push(
        pk.on(p.serverDraining, () => {
          if (!mine()) return;
          socket.forgetNode();
          this.events.emit('draining');
        }),
      );
    }
    if (p.result) this.offs.push(pk.on(p.result, (r) => mine() && this.events.emit('result', r)));
    const nothing = p.nothingToRejoin;
    if (nothing && typeof nothing !== 'function') this.offs.push(pk.on(nothing, () => mine() && this.concluded()));
    // A failed first connect also emits the socket's `close` (handled there as `connect`).
    socket.connect().catch(() => undefined);
  }

  private onStatus(s: TStatus): void {
    const nothing = this.o.protocol.nothingToRejoin;
    if (typeof nothing === 'function' && nothing(s)) {
      this.concluded();
      return;
    }
    this.events.emit('status', s);
    if (this._phase !== 'queueing') return;
    if (s.state === 'found') this.events.emit('found', s);
    else if (s.state === 'idle') {
      this.detach()?.close();
      this.toIdle();
      this.events.emit('stopped');
    }
  }

  private onWelcome(w: TWelcome): void {
    const rejoined = this._phase === 'rejoining' || this._phase === 'match';
    if (this.rejoinTimer) clearTimeout(this.rejoinTimer);
    this.rejoinTimer = null;
    this._roomId = w.roomId;
    this.rejoinTarget = null;
    if (this._socket) this._socket.autoReconnect = true;
    this.setPhase('match');
    this.events.emit('match', w, rejoined);
  }

  private concluded(): void {
    if (this._phase !== 'rejoining' && this._phase !== 'match') return;
    const room = this._roomId ?? this.rejoinTarget;
    this.detach()?.close();
    this.toIdle();
    this.events.emit('concluded', room);
  }

  private onClose(c: SocketClose, opened: boolean): void {
    const phase = this._phase;
    this.detach();
    this.toIdle();
    this.events.emit('error', !opened ? 'connect' : phase === 'match' ? 'disconnected' : 'lost', c);
  }
}
