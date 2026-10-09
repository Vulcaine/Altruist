/**
 * The host side of device pairing (C# `PairingPortal`): opens a session, shows its code, and talks
 * with every companion that joins (e.g. phones used as controllers of the game on a TV), each over its
 * own {@link PeerLink} (WebRTC when the network allows it, else the server relay).
 *
 * @example
 * ```ts
 * const host = new PairingHost({ url: sameOriginSocketUrl('/pair') });
 * host.on('hosted', (code) => showQr(`${location.origin}/?pad=${code}`));
 * host.on('join', (slot) => addController(slot));
 * host.on('message', (slot, bytes) => controllers[slot].receive(bytes));
 * await host.open();
 * ```
 */
import { AltruistSocket, type AltruistSocketOptions } from '../../../net/src/transport/index.ts';
import { Emitter } from '../../../net/src/util/emitter.ts';
import { HostedPacket, hostFrame, MessagePacket, PeerPacket, pingFrame, RejectedPacket, sendFrame, type PairingRejectReason } from './packets.ts';
import { localPeerConnection, PeerLink, type PairingTransport } from './peerLink.ts';

export interface PairingClientOptions {
  /** The pairing portal's socket URL (e.g. `sameOriginSocketUrl('/pair')`). */
  url: AltruistSocketOptions['url'];
  /** WebRTC factory; default {@link localPeerConnection} (null = relay only). */
  createPeerConnection?: (() => RTCPeerConnection) | null;
  /** Creates the WebSocket (tests). */
  createWebSocket?: AltruistSocketOptions['createWebSocket'];
  /** Keep-alive interval while nothing else is sent (default 10000 ms). */
  heartbeatMs?: number;
}

export type PairingHostEvents = {
  /** The session is open: show the code. */
  hosted: [code: string, maxCompanions: number];
  /** A companion joined in `slot`. */
  join: [slot: number];
  /** A companion left. */
  leave: [slot: number];
  message: [slot: number, bytes: Uint8Array];
  /** A companion's link moved to WebRTC or back to the relay. */
  transport: [slot: number, transport: PairingTransport];
  /** The peer-to-peer setup with a companion failed (it keeps working through the relay). */
  peerError: [slot: number, error: unknown];
  rejected: [reason: PairingRejectReason];
  /** The connection to the server ended: the session is gone. */
  closed: [];
};

export class PairingHost extends Emitter<PairingHostEvents> {
  private readonly socket: AltruistSocket;
  private readonly links = new Map<number, PeerLink>();
  private readonly createPc: (() => RTCPeerConnection) | null;
  private code: string | null = null;

  constructor(options: PairingClientOptions) {
    super();
    this.createPc = options.createPeerConnection === undefined ? localPeerConnection() : options.createPeerConnection;
    this.socket = new AltruistSocket({
      url: options.url,
      createWebSocket: options.createWebSocket,
      hello: () => hostFrame(),
      heartbeat: { frame: pingFrame, intervalMs: options.heartbeatMs ?? 10_000 },
    });
    this.socket.packets.on(HostedPacket, (p) => {
      this.code = p.code;
      this.emit('hosted', p.code, p.maxCompanions);
    });
    this.socket.packets.on(PeerPacket, (p) => (p.present ? this.addCompanion(p.slot) : this.removeCompanion(p.slot)));
    this.socket.packets.on(MessagePacket, (p) => this.links.get(p.from)?.receiveRelay(p.payload));
    this.socket.packets.on(RejectedPacket, (p) => this.emit('rejected', p.reason));
    this.socket.on('close', () => {
      for (const slot of [...this.links.keys()]) this.removeCompanion(slot);
      this.code = null;
      this.emit('closed');
    });
  }

  /** The session's code, once hosted. */
  get sessionCode(): string | null {
    return this.code;
  }

  /** Slots of the companions joined now. */
  companions(): number[] {
    return [...this.links.keys()].sort((a, b) => a - b);
  }

  transportOf(slot: number): PairingTransport | null {
    return this.links.get(slot)?.path ?? null;
  }

  /** Connects and opens the session ({@link PairingHostEvents.hosted} follows). */
  open(): Promise<void> {
    return this.socket.connect();
  }

  send(slot: number, bytes: Uint8Array): void {
    this.links.get(slot)?.send(bytes);
  }

  /** Closes the session: every companion hears it. */
  close(): void {
    for (const link of this.links.values()) link.close();
    this.links.clear();
    this.code = null;
    this.socket.close();
  }

  private addCompanion(slot: number): void {
    this.links.get(slot)?.close();
    this.links.set(
      slot,
      new PeerLink({
        relay: (frame) => this.socket.send(sendFrame(slot, frame)),
        createPeerConnection: this.createPc,
        initiator: false,
        onMessage: (bytes) => this.emit('message', slot, bytes),
        onTransport: (t) => this.emit('transport', slot, t),
        onPeerError: (e) => this.emit('peerError', slot, e),
      }),
    );
    this.emit('join', slot);
  }

  private removeCompanion(slot: number): void {
    const link = this.links.get(slot);
    if (!link) return;
    link.close();
    this.links.delete(slot);
    this.emit('leave', slot);
  }
}
