/**
 * One host <-> companion link of a pairing session. Messages go over a WebRTC data channel when one
 * opens (peer to peer on the local network: a few ms) and through the server relay (`pair-send`)
 * otherwise; the link signals the data channel through the relay too. Every message carries a
 * sequence number and is delivered in order, also across a switch between the two paths.
 *
 * Frames (both paths): `[0][seq u32 BE][bytes]` an app message, `[1][JSON]` a WebRTC signal (relay only).
 */

/** Which path messages take now. */
export type PairingTransport = 'relay' | 'p2p';

export interface PeerLinkOptions {
  /** Sends a frame through the server relay to the peer. */
  relay: (frame: Uint8Array) => void;
  /** Creates the WebRTC connection, or null for relay only (no WebRTC, tests, servers). */
  createPeerConnection: (() => RTCPeerConnection) | null;
  /** The side that creates the data channel and the offer (the companion). */
  initiator: boolean;
  onMessage: (bytes: Uint8Array) => void;
  onTransport: (transport: PairingTransport) => void;
  /** The peer-to-peer path failed to set up (messages keep going through the relay). */
  onPeerError: (error: unknown) => void;
  /** A missing message is waited for this long before later ones are delivered (default 300 ms). */
  holeMs?: number;
}

const APP = 0;
const SIGNAL = 1;
const CHANNEL = 'altruist-pair';

interface Signal {
  sdp?: RTCSessionDescriptionInit;
  ice?: RTCIceCandidateInit;
}

/** Delivers messages in sequence order; a hole older than `holeMs` is skipped (a lost message). */
export class InOrder {
  private next = 0;
  private readonly held = new Map<number, Uint8Array>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private readonly deliver: (bytes: Uint8Array) => void;
  private readonly holeMs: number;

  constructor(deliver: (bytes: Uint8Array) => void, holeMs: number) {
    this.deliver = deliver;
    this.holeMs = holeMs;
  }

  push(seq: number, bytes: Uint8Array): void {
    if (seq < this.next) return;
    this.held.set(seq, bytes);
    this.drain();
    if (this.held.size > 0 && !this.timer) this.timer = setTimeout(() => this.skipHole(), this.holeMs);
  }

  private drain(): void {
    for (let b = this.held.get(this.next); b; b = this.held.get(this.next)) {
      this.held.delete(this.next);
      this.next++;
      this.deliver(b);
    }
    if (this.held.size === 0 && this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private skipHole(): void {
    this.timer = null;
    if (this.held.size === 0) return;
    this.next = Math.min(...this.held.keys());
    this.drain();
    if (this.held.size > 0) this.timer = setTimeout(() => this.skipHole(), this.holeMs);
  }

  dispose(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.held.clear();
  }
}

const utf8 = new TextEncoder();
const fromUtf8 = new TextDecoder();

export class PeerLink {
  private pc: RTCPeerConnection | null = null;
  private channel: RTCDataChannel | null = null;
  private seq = 0;
  private readonly inOrder: InOrder;
  private readonly pendingIce: RTCIceCandidateInit[] = [];
  private transport: PairingTransport = 'relay';
  private closed = false;
  private readonly o: PeerLinkOptions;

  constructor(options: PeerLinkOptions) {
    this.o = options;
    this.inOrder = new InOrder(options.onMessage, options.holeMs ?? 300);
  }

  /** The path messages take now. */
  get path(): PairingTransport {
    return this.transport;
  }

  /** The initiator starts the peer-to-peer setup (no-op without WebRTC or on the answering side). */
  start(): void {
    if (!this.o.initiator || !this.o.createPeerConnection) return;
    const pc = this.connection();
    this.attach(pc.createDataChannel(CHANNEL, { ordered: true }));
    pc.createOffer()
      .then((offer) => pc.setLocalDescription(offer))
      .then(() => this.signal({ sdp: pc.localDescription!.toJSON() }))
      .catch((e: unknown) => this.o.onPeerError(e));
  }

  /** Sends an app message on the fastest open path. */
  send(bytes: Uint8Array): void {
    if (this.closed) return;
    const frame = new Uint8Array(5 + bytes.length);
    frame[0] = APP;
    new DataView(frame.buffer).setUint32(1, this.seq++);
    frame.set(bytes, 5);
    if (this.channel?.readyState === 'open') this.channel.send(frame);
    else this.o.relay(frame);
  }

  /** A frame the relay delivered from the peer. */
  receiveRelay(frame: Uint8Array): void {
    if (this.closed || frame.length === 0) return;
    if (frame[0] === APP) this.receiveApp(frame);
    else if (frame[0] === SIGNAL) this.receiveSignal(JSON.parse(fromUtf8.decode(frame.subarray(1))) as Signal);
  }

  close(): void {
    this.closed = true;
    this.inOrder.dispose();
    this.channel?.close();
    this.pc?.close();
    this.channel = null;
    this.pc = null;
  }

  private receiveApp(frame: Uint8Array): void {
    if (frame.length < 5) return;
    const seq = new DataView(frame.buffer, frame.byteOffset, frame.byteLength).getUint32(1);
    this.inOrder.push(seq, frame.subarray(5));
  }

  private receiveSignal(s: Signal): void {
    if (!this.o.createPeerConnection) return;
    const pc = this.connection();
    if (s.sdp) {
      pc.setRemoteDescription(s.sdp)
        .then(async () => {
          for (const c of this.pendingIce.splice(0)) await pc.addIceCandidate(c);
          if (s.sdp!.type !== 'offer') return;
          await pc.setLocalDescription(await pc.createAnswer());
          this.signal({ sdp: pc.localDescription!.toJSON() });
        })
        .catch((e: unknown) => this.o.onPeerError(e));
    }
    if (s.ice) {
      if (pc.remoteDescription) pc.addIceCandidate(s.ice).catch((e: unknown) => this.o.onPeerError(e));
      else this.pendingIce.push(s.ice);
    }
  }

  private connection(): RTCPeerConnection {
    if (this.pc) return this.pc;
    const pc = this.o.createPeerConnection!();
    pc.onicecandidate = (e) => {
      if (e.candidate) this.signal({ ice: e.candidate.toJSON() });
    };
    pc.ondatachannel = (e) => this.attach(e.channel);
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed') this.setTransport('relay');
    };
    this.pc = pc;
    return pc;
  }

  private attach(channel: RTCDataChannel): void {
    channel.binaryType = 'arraybuffer';
    channel.onopen = () => this.setTransport('p2p');
    channel.onclose = () => this.setTransport('relay');
    channel.onmessage = (e) => {
      const frame = new Uint8Array(e.data as ArrayBuffer);
      if (frame[0] === APP) this.receiveApp(frame);
    };
    this.channel = channel;
  }

  private setTransport(t: PairingTransport): void {
    if (t === this.transport || this.closed) return;
    this.transport = t;
    if (t === 'relay') this.channel = null;
    this.o.onTransport(t);
  }

  private signal(s: Signal): void {
    const json = utf8.encode(JSON.stringify(s));
    const frame = new Uint8Array(1 + json.length);
    frame[0] = SIGNAL;
    frame.set(json, 1);
    this.o.relay(frame);
  }
}

/** WebRTC with no STUN / TURN servers: host and phone on one network reach each other directly. */
export function localPeerConnection(): (() => RTCPeerConnection) | null {
  return typeof RTCPeerConnection === 'function' ? () => new RTCPeerConnection({ iceServers: [] }) : null;
}
