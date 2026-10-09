import assert from 'node:assert/strict';
import { describe, it, type TestContext } from 'node:test';
import {
  AltruistSocket,
  decodeMsgPack,
  directSocketUrl,
  encodeEnvelope,
  EndpointFailover,
  MemoryStorage,
  NetConditioner,
  packetFrame,
  parseClientFrame,
  sameOriginSocketUrl,
  serverPacket,
  SocketConnectError,
  ticketUrl,
  withNode,
  type SocketClose,
  type SocketState,
  type WebSocketLike,
} from '../src/index.ts';

/** A scripted WebSocket: tests open, message and close it by hand. */
class FakeWs implements WebSocketLike {
  static all: FakeWs[] = [];
  binaryType = 'blob';
  readyState = 0;
  sent: Uint8Array[] = [];
  closedWith: number | undefined;
  onopen: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onclose: ((ev: { code: number }) => void) | null = null;
  readonly url: string;
  constructor(url: string) {
    this.url = url;
    FakeWs.all.push(this);
  }
  send(data: Uint8Array): void {
    this.sent.push(data);
  }
  close(code?: number): void {
    this.closedWith = code;
    if (this.readyState === 3) return;
    const wasOpen = this.readyState === 1;
    this.readyState = 3;
    if (wasOpen) this.onclose?.({ code: code ?? 1005 });
  }
  open(): void {
    this.readyState = 1;
    this.onopen?.({});
  }
  fail(): void {
    this.onerror?.({});
    this.readyState = 3;
    this.onclose?.({ code: 1006 });
  }
  drop(code: number): void {
    this.readyState = 3;
    this.onclose?.({ code });
  }
  receive(bytes: Uint8Array): void {
    this.onmessage?.({ data: bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) });
  }
  get events(): string[] {
    return this.sent.map((b) => parseClientFrame(b)!.event);
  }
}

const flush = () => new Promise<void>((r) => setImmediate(r));
const last = () => FakeWs.all[FakeWs.all.length - 1]!;

function setup(t: TestContext) {
  FakeWs.all = [];
  t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'] });
}

const Welcome = serverPacket(2001, (m) => ({ room: String(m[1]) }));

function make(opts: Partial<ConstructorParameters<typeof AltruistSocket>[0]> = {}) {
  let n = 0;
  const states: SocketState[] = [];
  const closes: SocketClose[] = [];
  const socket = new AltruistSocket({
    url: async () => `wss://game.test/play?ticket=t${++n}`,
    createWebSocket: (u) => new FakeWs(u),
    hello: ({ reason }) => packetFrame(reason, 1),
    now: () => Date.now(),
    ...opts,
  });
  socket.on('state', (s) => states.push(s));
  socket.on('close', (c) => closes.push(c));
  return { socket, states, closes };
}

async function connected(t: TestContext, opts: Partial<ConstructorParameters<typeof AltruistSocket>[0]> = {}) {
  setup(t);
  const r = make(opts);
  const p = r.socket.connect();
  await flush();
  last().open();
  await p;
  return r;
}

describe('AltruistSocket', () => {
  it('connects with a fresh URL, sends the hello, dispatches frames', async (t) => {
    const { socket, states } = await connected(t);
    assert.equal(last().url, 'wss://game.test/play?ticket=t1');
    assert.equal(last().binaryType, 'arraybuffer');
    assert.deepEqual(last().events, ['connect']);
    assert.equal(socket.state, 'open');
    assert.deepEqual(states, ['connecting', 'open']);
    let room = '';
    socket.packets.on(Welcome, (w) => (room = w.room));
    const frames: number[] = [];
    socket.on('frame', (b) => frames.push(b.length));
    last().receive(encodeEnvelope(2001, [2001, 'r9']));
    assert.equal(room, 'r9');
    assert.equal(frames.length, 1);
    assert.ok(socket.send(packetFrame('chat', 9, 'hi')));
    assert.ok(socket.sendEvent('emote', [10, 3]));
    assert.deepEqual(last().events, ['connect', 'chat', 'emote']);
    assert.deepEqual(decodeMsgPack(parseClientFrame(last().sent[2]!)!.payload), [10, 3]);
  });

  it('first-connect failure rejects and closes with unreachable; timeout too', async (t) => {
    setup(t);
    const a = make();
    const p = a.socket.connect();
    await flush();
    last().fail();
    await assert.rejects(p, SocketConnectError);
    assert.equal(a.socket.state, 'closed');
    assert.equal(a.closes[0]!.reason, 'unreachable');
    assert.equal(a.socket.send(packetFrame('x', 1)), false);

    const b = make({ connectTimeoutMs: 1000 });
    const q = b.socket.connect();
    await flush();
    t.mock.timers.tick(1000);
    await assert.rejects(q, (e: SocketConnectError) => e.kind === 'timeout');
  });

  it('a 401 from the URL provider ends with unauthorized', async (t) => {
    setup(t);
    const { socket, closes } = make({ url: async () => Promise.reject(Object.assign(new Error('no'), { status: 401 })) });
    await assert.rejects(socket.connect());
    assert.equal(closes[0]!.reason, 'unauthorized');
  });

  it('without autoReconnect a drop is final', async (t) => {
    const { socket, closes } = await connected(t);
    last().drop(1006);
    assert.equal(socket.state, 'closed');
    assert.deepEqual(closes, [{ reason: 'closed', code: 1006 }]);
  });

  it('reconnects retryable drops with backoff, then waits for the rejoin confirmation', async (t) => {
    const { socket, states } = await connected(t, { autoReconnect: true, rejoinConfirmCodes: [2001] });
    const drops: number[] = [];
    socket.on('drop', (c) => drops.push(c));
    last().drop(1006);
    assert.equal(socket.state, 'reconnecting');
    t.mock.timers.tick(399);
    await flush();
    assert.equal(FakeWs.all.length, 1);
    t.mock.timers.tick(1);
    await flush();
    assert.equal(FakeWs.all.length, 2);
    assert.equal(last().url, 'wss://game.test/play?ticket=t2');
    last().open();
    await flush();
    assert.deepEqual(last().events, ['reconnect']);
    assert.equal(socket.state, 'reconnecting');
    last().receive(encodeEnvelope(2001, [2001, 'r']));
    assert.equal(socket.state, 'open');
    assert.deepEqual(states, ['connecting', 'open', 'reconnecting', 'rejoined', 'open']);
    assert.deepEqual(drops, [1006]);
  });

  it('rejoinFailed after the wait; failed sockets retry until the grace ends', async (t) => {
    const { socket, closes } = await connected(t, { autoReconnect: true, rejoinWaitMs: 1000, graceMs: 3000 });
    last().drop(1012);
    t.mock.timers.tick(400);
    await flush();
    last().open();
    await flush();
    t.mock.timers.tick(1000);
    assert.equal(socket.state, 'rejoinFailed');
    socket.confirmRejoin();
    assert.equal(socket.state, 'open');
    // a second outage: every attempt fails until the grace runs out
    last().drop(1006);
    for (let i = 0; i < 10 && socket.state !== 'closed'; i++) {
      t.mock.timers.tick(4000);
      await flush();
      if (last().readyState === 0) last().fail();
      await flush();
    }
    assert.equal(socket.state, 'closed');
    assert.equal(closes[0]!.reason, 'grace-expired');
  });

  it('non-retryable close codes are final even with autoReconnect', async (t) => {
    const { closes } = await connected(t, { autoReconnect: true });
    last().drop(4001);
    assert.deepEqual(closes, [{ reason: 'closed', code: 4001 }]);
  });

  it('a fatal error while reconnecting ends with unauthorized', async (t) => {
    let calls = 0;
    const { closes } = await connected(t, {
      autoReconnect: true,
      url: async () => {
        if (++calls > 1) throw Object.assign(new Error('expired'), { status: 403 });
        return 'wss://game.test/x';
      },
    });
    last().drop(1006);
    t.mock.timers.tick(400);
    await flush();
    await flush();
    assert.equal(closes[0]!.reason, 'unauthorized');
  });

  it('redirects to another fleet server and keeps routing there', async (t) => {
    const { socket } = await connected(t, { autoReconnect: true });
    const first = last();
    const redirects: string[] = [];
    socket.on('redirect', (r) => redirects.push(r.nodeId));
    socket.redirect({ nodeId: 'n2', nodeParam: 'node', publicAddress: null, reason: 'queue' });
    await flush();
    const second = last();
    assert.equal(second.url, 'wss://game.test/play?ticket=t2&node=n2');
    second.open();
    await flush();
    assert.equal(first.closedWith, 1000);
    assert.deepEqual(second.events, ['redirect']);
    assert.equal(socket.state, 'open');
    assert.deepEqual(redirects, ['n2']);
    // reconnects keep the node until forgotten
    second.drop(1006);
    t.mock.timers.tick(400);
    await flush();
    assert.match(last().url, /node=n2/);
    last().open();
    await flush();
    socket.confirmRejoin();
    socket.forgetNode();
    last().drop(1006);
    t.mock.timers.tick(400);
    await flush();
    assert.doesNotMatch(last().url, /node=/);
  });

  it('heartbeat pings only after silence', async (t) => {
    const { socket } = await connected(t, { heartbeat: { frame: () => packetFrame('ping', 3), intervalMs: 1000 } });
    t.mock.timers.tick(750);
    socket.send(packetFrame('input', 2));
    t.mock.timers.tick(750);
    assert.deepEqual(last().events, ['connect', 'input']);
    t.mock.timers.tick(1000);
    assert.deepEqual(last().events, ['connect', 'input', 'ping']);
  });

  it('close() is final and silent', async (t) => {
    const { socket, closes, states } = await connected(t, { autoReconnect: true });
    socket.close();
    socket.close();
    assert.equal(last().closedWith, 1000);
    assert.equal(socket.state, 'closed');
    assert.equal(closes.length, 0);
    assert.equal(states.at(-1), 'closed');
  });

  it('fails over from a primary that does not open to the fallback', async (t) => {
    setup(t);
    const failover = new EndpointFailover({ primary: 'wss://direct.test/g', fallback: 'wss://edge.test/g', connectTimeoutMs: 500, now: () => Date.now() });
    const { socket } = make({ url: () => failover.current(), failover });
    const p = socket.connect();
    await flush();
    assert.equal(last().url, 'wss://direct.test/g');
    t.mock.timers.tick(500);
    await flush();
    await flush();
    assert.equal(last().url, 'wss://edge.test/g');
    last().open();
    await p;
    assert.equal(failover.current(), 'wss://edge.test/g');
  });

  it('frames pass through the conditioner with the simulated delay', async (t) => {
    let clockT = 0;
    const timers: { at: number; fn: () => void }[] = [];
    const clock = {
      now: () => clockT,
      setTimeout: (fn: () => void, ms: number) => (timers.push({ at: clockT + ms, fn }), timers.length),
      clearTimeout: () => {},
    };
    const conditioner = new NetConditioner({ clock, random: () => 0.5 });
    conditioner.update({ enabled: true, rttMs: 100 });
    const { socket } = await connected(t, { conditioner });
    assert.equal(last().sent.length, 0); // hello held 50 ms
    let sim = -1;
    socket.packets.on(Welcome, (_w, ctx) => (sim = ctx.simulatedMs));
    last().receive(encodeEnvelope(2001, [2001, 'r']));
    assert.equal(sim, -1);
    clockT = 50;
    for (const tm of timers.splice(0)) tm.fn();
    assert.equal(last().sent.length, 1);
    assert.equal(sim, 100);
  });
});

describe('endpoint helpers', () => {
  it('withNode: shared address with ?node=, or the node public address keeping the ticket', () => {
    assert.equal(withNode('wss://a.test/g?ticket=x', null), 'wss://a.test/g?ticket=x');
    assert.equal(withNode('wss://a.test/g?ticket=x', { nodeId: 'n1', nodeParam: 'srv', publicAddress: null, reason: '' }), 'wss://a.test/g?ticket=x&srv=n1');
    assert.equal(withNode('wss://a.test/g?ticket=x', { nodeId: 'n1', nodeParam: 'node', publicAddress: 'wss://n1.test/g', reason: '' }), 'wss://n1.test/g?ticket=x&node=n1');
  });

  it('ticketUrl adds a fresh ticket, tolerates a missing endpoint when allowed', async () => {
    let n = 0;
    const url = ticketUrl({ base: 'wss://a.test/g', fetchTicket: async () => ({ ticket: `t ${++n}` }) });
    assert.equal(await url(), 'wss://a.test/g?ticket=t+1');
    assert.equal(await url(), 'wss://a.test/g?ticket=t+2');
    const custom = ticketUrl({ base: () => 'wss://b.test/g', fetchTicket: async () => 'raw', queryParam: 'tk' });
    assert.equal(await custom(), 'wss://b.test/g?tk=raw');
    const missing = Object.assign(new Error('404'), { status: 404 });
    const lenient = ticketUrl({ base: 'wss://a.test/g', fetchTicket: () => Promise.reject(missing), allowMissingEndpoint: true });
    assert.equal(await lenient(), 'wss://a.test/g');
    const strict = ticketUrl({ base: 'wss://a.test/g', fetchTicket: () => Promise.reject(missing) });
    await assert.rejects(strict(), /404/);
  });

  it('direct and same-origin URLs', () => {
    const page = { protocol: 'https:', host: 'game.test', hostname: 'game.test' };
    assert.equal(sameOriginSocketUrl('/game', page), 'wss://game.test/game');
    assert.equal(sameOriginSocketUrl('game', { ...page, protocol: 'http:' }), 'ws://game.test/game');
    assert.equal(directSocketUrl('wss://play.test/game', page), 'wss://play.test/game');
    assert.equal(directSocketUrl('ws://play.test/game', page), null);
    assert.equal(directSocketUrl('https://play.test/game', page), null);
    assert.equal(directSocketUrl('wss://game.test/other', page), null);
    assert.equal(directSocketUrl('wss://play.test/game', { protocol: 'http:', host: 'localhost:5173', hostname: 'localhost' }), null);
    assert.equal(directSocketUrl('  ', page), null);
    assert.equal(directSocketUrl('not a url', page), null);
  });

  it('EndpointFailover remembers a failure for retryAfterMs and persists it', () => {
    let now = 1000;
    const storage = new MemoryStorage();
    const f = new EndpointFailover({ primary: 'wss://d.test/g', fallback: 'wss://e.test/g', retryAfterMs: 100, storageKey: 'down', storage, now: () => now });
    assert.equal(f.current(), 'wss://d.test/g');
    assert.ok(f.isPrimary('wss://d.test/other?x=1'));
    assert.ok(!f.isPrimary('wss://e.test/g'));
    assert.ok(f.markDown());
    assert.ok(!f.markDown());
    assert.equal(f.current(), 'wss://e.test/g');
    assert.equal(new EndpointFailover({ primary: 'wss://d.test/g', fallback: 'x', storageKey: 'down', storage, now: () => now }).current(), 'x');
    now += 100;
    assert.equal(f.current(), 'wss://d.test/g');
    f.reset();
    assert.equal(storage.getItem('down'), null);
    const none = new EndpointFailover({ primary: null, fallback: 'wss://e.test/g' });
    assert.equal(none.markDown(), false);
    assert.equal(none.current(), 'wss://e.test/g');
  });
});
