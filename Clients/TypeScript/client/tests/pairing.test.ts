import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { decodeMsgPack, encodeEnvelope, parseClientFrame, type WebSocketLike } from '../../net/src/index.ts';
import { InOrder, PairingCodes, PairingCompanion, PairingHost, pairingRejectReasonName } from '../src/index.ts';

/**
 * A fake pairing server: the C# PairingSessions rules (host slot 0, companions 1.., relay, a host
 * leaving closes the session), over fake sockets that deliver synchronously.
 */
class FakeServer {
  private readonly sockets: FakeWs[] = [];
  private host: FakeWs | null = null;
  private code = 'ABC234';
  private readonly companions = new Map<number, FakeWs>();

  connect(url: string): FakeWs {
    const ws = new FakeWs(url, this);
    this.sockets.push(ws);
    queueMicrotask(() => ws.open());
    return ws;
  }

  receive(from: FakeWs, frame: Uint8Array): void {
    const parsed = parseClientFrame(frame)!;
    const m = decodeMsgPack(parsed.payload) as unknown[];
    if (parsed.event === 'pair-host') {
      this.host = from;
      from.push(PairingCodes.hosted, [this.code, 4]);
    } else if (parsed.event === 'pair-join') {
      if (m[1] !== this.code) return from.push(PairingCodes.rejected, [1]);
      const slot = [1, 2, 3, 4].find((s) => !this.companions.has(s))!;
      this.companions.set(slot, from);
      from.push(PairingCodes.joined, [slot]);
      this.host!.push(PairingCodes.peer, [slot, true]);
    } else if (parsed.event === 'pair-send') {
      const fromSlot = from === this.host ? 0 : [...this.companions].find(([, ws]) => ws === from)![0];
      const target = m[1] === 0 ? this.host! : this.companions.get(m[1] as number)!;
      target.push(PairingCodes.message, [fromSlot, m[2]]);
    }
  }

  disconnect(ws: FakeWs): void {
    if (ws === this.host) {
      for (const c of this.companions.values()) c.push(PairingCodes.peer, [0, false]);
      this.companions.clear();
      this.host = null;
      return;
    }
    for (const [slot, c] of this.companions) {
      if (c !== ws) continue;
      this.companions.delete(slot);
      this.host?.push(PairingCodes.peer, [slot, false]);
    }
  }
}

class FakeWs implements WebSocketLike {
  binaryType = 'blob';
  readyState = 0;
  onopen: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onclose: ((ev: { code: number }) => void) | null = null;
  readonly url: string;
  private readonly server: FakeServer;
  constructor(url: string, server: FakeServer) {
    this.url = url;
    this.server = server;
  }
  send(data: Uint8Array): void {
    this.server.receive(this, data);
  }
  close(): void {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.server.disconnect(this);
  }
  open(): void {
    this.readyState = 1;
    this.onopen?.({});
  }
  push(code: number, fields: unknown[]): void {
    if (this.readyState !== 1) return;
    this.onmessage?.({ data: encodeEnvelope(code, [code, ...fields]).buffer });
  }
}

const flush = () => new Promise<void>((r) => setImmediate(r));
const bytes = (...b: number[]) => new Uint8Array(b);

function pair(server: FakeServer) {
  const options = { url: 'ws://test/pair', createPeerConnection: null, createWebSocket: (url: string) => server.connect(url) };
  return { host: new PairingHost(options), phone: () => new PairingCompanion(options) };
}

describe('pairing over the relay', () => {
  it('the host gets a code, a companion joins it and they talk both ways', async () => {
    const server = new FakeServer();
    const { host, phone } = pair(server);
    let code = '';
    const atHost: [number, number[]][] = [];
    host.on('hosted', (c) => (code = c));
    host.on('message', (slot, b) => atHost.push([slot, [...b]]));
    await host.open();
    await flush();
    assert.equal(code, 'ABC234');

    const pad = phone();
    const atPhone: number[][] = [];
    pad.on('message', (b) => atPhone.push([...b]));
    let joined = 0;
    pad.on('joined', (s) => (joined = s));
    await pad.join(' abc234 ');
    await flush();
    assert.equal(joined, 1);
    assert.deepEqual(host.companions(), [1]);

    pad.send(bytes(1, 2, 3));
    pad.send(bytes(4));
    host.send(1, bytes(9));
    assert.deepEqual(atHost, [
      [1, [1, 2, 3]],
      [1, [4]],
    ]);
    assert.deepEqual(atPhone, [[9]]);
    assert.equal(pad.transport, 'relay');
    host.close();
  });

  it('several companions get their own slots; one leaving frees it', async () => {
    const server = new FakeServer();
    const { host, phone } = pair(server);
    const left: number[] = [];
    host.on('leave', (s) => left.push(s));
    await host.open();
    await flush();
    const a = phone();
    const b = phone();
    await a.join('ABC234');
    await b.join('ABC234');
    await flush();
    assert.deepEqual(host.companions(), [1, 2]);
    a.leave();
    assert.deepEqual(left, [1]);
    assert.deepEqual(host.companions(), [2]);
    host.close();
  });

  it('a wrong code is rejected and closes the companion', async () => {
    const server = new FakeServer();
    const { host, phone } = pair(server);
    await host.open();
    await flush();
    const pad = phone();
    const events: string[] = [];
    pad.on('rejected', (r) => events.push(r));
    pad.on('closed', () => events.push('closed'));
    await pad.join('WRONG1');
    await flush();
    assert.deepEqual(events, ['unknown-code', 'closed']);
    host.close();
  });

  it('the host closing ends every companion', async () => {
    const server = new FakeServer();
    const { host, phone } = pair(server);
    await host.open();
    await flush();
    const pad = phone();
    let closed = false;
    pad.on('closed', () => (closed = true));
    await pad.join('ABC234');
    await flush();
    host.close();
    assert.equal(closed, true);
  });
});

describe('in-order delivery', () => {
  it('holds a message that came early until the missing one arrives', () => {
    const got: number[] = [];
    const o = new InOrder((b) => got.push(b[0]!), 1000);
    o.push(0, bytes(10));
    o.push(2, bytes(12));
    assert.deepEqual(got, [10]);
    o.push(1, bytes(11));
    assert.deepEqual(got, [10, 11, 12]);
    o.push(1, bytes(99));
    assert.deepEqual(got, [10, 11, 12]);
    o.dispose();
  });

  it('skips a hole that never fills', async () => {
    const got: number[] = [];
    const o = new InOrder((b) => got.push(b[0]!), 5);
    o.push(0, bytes(10));
    o.push(3, bytes(13));
    o.push(4, bytes(14));
    await new Promise((r) => setTimeout(r, 20));
    assert.deepEqual(got, [10, 13, 14]);
    o.dispose();
  });
});

describe('reject reasons', () => {
  it('name the C# values', () => {
    assert.equal(pairingRejectReasonName(2), 'full');
    assert.equal(pairingRejectReasonName(99), 'unknown');
  });
});
