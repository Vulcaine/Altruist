/**
 * The companion side of device pairing (C# `PairingPortal`): joins a host's session with its code
 * (e.g. a phone becoming a controller of the game on a TV) and talks with the host over a
 * {@link PeerLink} (WebRTC when the network allows it, else the server relay).
 *
 * @example
 * ```ts
 * const pad = new PairingCompanion({ url: sameOriginSocketUrl('/pair') });
 * pad.on('joined', (slot) => showPad(slot));
 * pad.on('closed', () => showGame());
 * await pad.join(code);
 * pad.send(encodeTouch(event));
 * ```
 */
import { AltruistSocket } from '../../../net/src/transport/index.ts';
import { Emitter } from '../../../net/src/util/emitter.ts';
import { JoinedPacket, joinFrame, MessagePacket, PeerPacket, pingFrame, RejectedPacket, sendFrame, type PairingRejectReason } from './packets.ts';
import type { PairingClientOptions } from './pairingHost.ts';
import { localPeerConnection, PeerLink, type PairingTransport } from './peerLink.ts';

export type PairingCompanionEvents = {
  /** Joined the session in `slot`. */
  joined: [slot: number];
  message: [bytes: Uint8Array];
  transport: [transport: PairingTransport];
  /** The peer-to-peer setup failed (it keeps working through the relay). */
  peerError: [error: unknown];
  rejected: [reason: PairingRejectReason];
  /** The session ended: the host left, the server connection ended, or {@link PairingCompanion.leave}. */
  closed: [];
};

export class PairingCompanion extends Emitter<PairingCompanionEvents> {
  private readonly socket: AltruistSocket;
  private readonly createPc: (() => RTCPeerConnection) | null;
  private link: PeerLink | null = null;
  private code = '';
  private slotNo = 0;
  private ended = false;

  constructor(options: PairingClientOptions) {
    super();
    this.createPc = options.createPeerConnection === undefined ? localPeerConnection() : options.createPeerConnection;
    this.socket = new AltruistSocket({
      url: options.url,
      createWebSocket: options.createWebSocket,
      hello: () => joinFrame(this.code),
      heartbeat: { frame: pingFrame, intervalMs: options.heartbeatMs ?? 10_000 },
    });
    this.socket.packets.on(JoinedPacket, (p) => this.joined(p.slot));
    this.socket.packets.on(MessagePacket, (p) => this.link?.receiveRelay(p.payload));
    this.socket.packets.on(PeerPacket, (p) => {
      if (p.slot === 0 && !p.present) this.end();
    });
    this.socket.packets.on(RejectedPacket, (p) => {
      this.emit('rejected', p.reason);
      if (!this.link) this.end();
    });
    this.socket.on('close', () => this.end());
  }

  /** The slot joined (1..), or 0 before joining. */
  get slot(): number {
    return this.slotNo;
  }

  get transport(): PairingTransport | null {
    return this.link?.path ?? null;
  }

  /** Connects and joins the session with `code` ({@link PairingCompanionEvents.joined} or `rejected` follows). */
  join(code: string): Promise<void> {
    this.code = code.trim().toUpperCase();
    return this.socket.connect();
  }

  send(bytes: Uint8Array): void {
    this.link?.send(bytes);
  }

  /** Leaves the session (the host hears it). */
  leave(): void {
    this.end();
  }

  private joined(slot: number): void {
    this.slotNo = slot;
    this.link = new PeerLink({
      relay: (frame) => this.socket.send(sendFrame(0, frame)),
      createPeerConnection: this.createPc,
      initiator: true,
      onMessage: (bytes) => this.emit('message', bytes),
      onTransport: (t) => this.emit('transport', t),
      onPeerError: (e) => this.emit('peerError', e),
    });
    this.emit('joined', slot);
    this.link.start();
  }

  private end(): void {
    if (this.ended) return;
    this.ended = true;
    this.link?.close();
    this.link = null;
    this.socket.close();
    this.emit('closed');
  }
}
