import assert from 'node:assert/strict';
import { describe, it, type TestContext } from 'node:test';
import { encodeEnvelope, packetFrame, parseClientFrame, serverPacket, type WebSocketLike } from '../../net/src/index.ts';
import {
  LOBBY_REJECT_REASONS,
  lobbyRejectReasonName,
  MatchmakingClient,
  QUEUE_STATES,
  queueStateName,
  remainingSeconds,
  ROOM_NOTICE_KINDS,
  roomNoticeKindName,
  StatusPoller,
  type MatchPhase,
  type QueueStatusInfo,
} from '../src/index.ts';

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
    this.closedWith = code ?? 1005;
    this.readyState = 3;
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
  push(code: number, fields: unknown[]): void {
    const b = encodeEnvelope(code, [code, ...fields]);
    this.onmessage?.({ data: b.buffer });
  }
  get events(): string[] {
    return this.sent.map((b) => parseClientFrame(b)!.event);
  }
}

const flush = () => new Promise<void>((r) => setImmediate(r));
const last = () => FakeWs.all[FakeWs.all.length - 1]!;

type Status = QueueStatusInfo<'banned' | 'concluded'> & { retryIn: number };

const protocol = {
  queue: (p: string) => packetFrame('queue', 1007, p),
  cancelQueue: () => packetFrame('cancelqueue', 1008),
  rejoin: (room: string | null) => packetFrame('rejoin', 1010, room ?? ''),
  leave: () => packetFrame('return', 1005),
  queueStatus: serverPacket(2007, (m) => ({
    state: (queueStateName(m[1]) ?? String(m[1])) as Status['state'],
    playlist: String(m[2]),
    elapsed: Number(m[3]),
    playersFound: Number(m[4]),
    playersNeeded: Number(m[5]),
    retryIn: Number(m[6] ?? 0),
  })),
  welcome: serverPacket(2001, (m) => ({ roomId: String(m[1]) })),
  lobbyState: serverPacket(2006, (m) => ({ code: String(m[1]) })),
  lobbyReject: serverPacket(2004, (m) => ({ reason: String(m[1]) })),
  notice: serverPacket(2009, (m) => ({ kind: roomNoticeKindName(m[1]) ?? 'left', seat: Number(m[2]), name: String(m[3]) })),
  redirect: serverPacket(2011, (m) => ({ nodeId: String(m[1]), nodeParam: String(m[2]), publicAddress: null, reason: String(m[3]) })),
  serverDraining: serverPacket(2012, () => null),
  result: serverPacket(2010, (m) => ({ won: m[1] === true })),
  nothingToRejoin: (s: Status) => s.state === 'concluded',
};

function make(t: TestContext) {
  FakeWs.all = [];
  t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'] });
  let n = 0;
  const mm = new MatchmakingClient({
    protocol,
    socket: { url: async () => `wss://g.test/game?ticket=${++n}`, createWebSocket: (u) => new FakeWs(u), now: () => Date.now() },
    rejoinWaitMs: 1000,
  });
  const log: string[] = [];
  const phases: MatchPhase[] = [];
  mm.on('phase', (p) => phases.push(p));
  mm.on('status', (s) => log.push(`status:${s.state}`));
  mm.on('found', () => log.push('found'));
  mm.on('match', (w, re) => log.push(`match:${w.roomId}:${re}`));
  mm.on('lobby', (l) => log.push(`lobby:${(l as { code: string }).code}`));
  mm.on('lobbyRejected', (r) => log.push(`rejected:${r}`));
  mm.on('notice', (n2) => log.push(`notice:${n2.kind}:${n2.name}`));
  mm.on('concluded', (r) => log.push(`concluded:${r}`));
  mm.on('stopped', () => log.push('stopped'));
  mm.on('draining', () => log.push('draining'));
  mm.on('result', (r) => log.push(`result:${(r as { won: boolean }).won}`));
  mm.on('rejoinTimeout', (r) => log.push(`rejoinTimeout:${r}`));
  mm.on('error', (k) => log.push(`error:${k}`));
  return { mm, log, phases };
}

describe('Altruist enums', () => {
  it('mirror the C# members (index = numeric value) and parse wire spellings', () => {
    assert.deepEqual(QUEUE_STATES, ['idle', 'searching', 'found']);
    assert.deepEqual(LOBBY_REJECT_REASONS, ['notFound', 'full', 'unavailable', 'tooManyPlayers']);
    assert.deepEqual(ROOM_NOTICE_KINDS, ['joining', 'left', 'botIn', 'botOut', 'reconnected']);
    assert.equal(queueStateName(1), 'searching');
    assert.equal(queueStateName('Found'), 'found');
    assert.equal(queueStateName('banned'), null);
    assert.equal(queueStateName(9), null);
    assert.equal(lobbyRejectReasonName('TooManyPlayers'), 'tooManyPlayers');
    assert.equal(lobbyRejectReasonName('not_found'), 'notFound');
    assert.equal(roomNoticeKindName(3), 'botOut');
    assert.equal(roomNoticeKindName(null), null);
  });
});

describe('MatchmakingClient', () => {
  it('queue → found → match → result → post', async (t) => {
    const { mm, log, phases } = make(t);
    mm.queue('duel');
    assert.equal(mm.phase, 'queueing');
    await flush();
    last().open();
    await flush();
    assert.deepEqual(last().events, ['queue']);
    last().push(2007, ['searching', 'duel', 3, 1, 2]);
    last().push(2007, ['found', 'duel', 4, 2, 2]);
    last().push(2001, ['room-1']);
    assert.equal(mm.phase, 'match');
    assert.equal(mm.roomId, 'room-1');
    assert.equal(mm.socket?.autoReconnect, true);
    last().push(2009, ['joining', 2, 'Kim']);
    last().push(2010, [true]);
    mm.finish();
    assert.equal(mm.phase, 'post');
    assert.equal(last().closedWith, 1000);
    mm.reset();
    assert.deepEqual(log, ['status:searching', 'status:found', 'found', 'match:room-1:false', 'notice:joining:Kim', 'result:true']);
    assert.deepEqual(phases, ['queueing', 'match', 'post', 'idle']);
  });

  it('a mid-match drop reconnects with a rejoin hello and rejoins on the welcome', async (t) => {
    const { mm, log } = make(t);
    mm.queue('duel');
    await flush();
    last().open();
    await flush();
    last().push(2001, ['room-1']);
    last().drop(1006);
    t.mock.timers.tick(400);
    await flush();
    assert.equal(FakeWs.all.length, 2);
    last().open();
    await flush();
    assert.deepEqual(last().events, ['rejoin']);
    assert.deepEqual(parseClientFrame(last().sent[0]!)!.event, 'rejoin');
    assert.equal(mm.socket?.state, 'reconnecting');
    last().push(2001, ['room-1']);
    assert.equal(mm.socket?.state, 'open');
    assert.deepEqual(log, ['match:room-1:false', 'match:room-1:true']);
  });

  it('cancel sends the cancel frame and closes after the grace', async (t) => {
    const { mm } = make(t);
    mm.queue('duel');
    await flush();
    const ws = last();
    ws.open();
    await flush();
    mm.cancel();
    assert.equal(mm.phase, 'idle');
    assert.deepEqual(ws.events, ['queue', 'cancelqueue']);
    assert.equal(ws.closedWith, undefined);
    t.mock.timers.tick(250);
    assert.equal(ws.closedWith, 1000);
  });

  it('queueing another playlist replaces the queue; the same one is ignored; idle from the server stops', async (t) => {
    const { mm, log } = make(t);
    mm.queue('duel');
    mm.queue('duel');
    await flush();
    assert.equal(FakeWs.all.length, 1);
    last().open();
    await flush();
    mm.queue('trio');
    await flush();
    assert.equal(FakeWs.all.length, 2);
    assert.equal(mm.playlist, 'trio');
    last().open();
    await flush();
    last().push(2007, ['idle', 'trio', 0, 0, 3]);
    assert.equal(mm.phase, 'idle');
    assert.ok(log.includes('stopped'));
  });

  it('rejoin: concluded when there is nothing to rejoin; timeout event otherwise', async (t) => {
    const { mm, log } = make(t);
    mm.rejoin('room-9', 'duel');
    await flush();
    last().open();
    await flush();
    assert.deepEqual(last().events, ['rejoin']);
    t.mock.timers.tick(1000);
    assert.deepEqual(log, ['rejoinTimeout:room-9']);
    last().push(2007, ['concluded', '', 0, 0, 0]);
    assert.equal(mm.phase, 'idle');
    assert.deepEqual(log, ['rejoinTimeout:room-9', 'concluded:room-9']);
  });

  it('game-specific statuses (bans) only go to the status event', async (t) => {
    const { mm, log } = make(t);
    let retry = 0;
    mm.on('status', (s) => (retry = s.retryIn));
    mm.queue('ranked');
    await flush();
    last().open();
    await flush();
    last().push(2007, ['banned', 'ranked', 0, 0, 2, 120]);
    assert.equal(retry, 120);
    assert.equal(mm.phase, 'queueing');
    assert.deepEqual(log, ['status:banned']);
  });

  it('lobby: state, rejection before joining resets, leave from a lobby', async (t) => {
    const { mm, log } = make(t);
    mm.lobby(packetFrame('join', 1001, 'ABCDE'));
    await flush();
    last().open();
    await flush();
    assert.deepEqual(last().events, ['join']);
    last().push(2004, ['full']);
    assert.equal(mm.phase, 'idle');
    mm.lobby(packetFrame('join', 1001, ''));
    await flush();
    last().open();
    await flush();
    last().push(2006, ['QWERT']);
    last().push(2004, ['NotFound']);
    assert.equal(mm.phase, 'lobby');
    mm.leave();
    assert.equal(mm.phase, 'idle');
    assert.deepEqual(log, ['rejected:full', 'lobby:QWERT', 'rejected:notFound']);
  });

  it('follows redirects and forgets the node when the server drains', async (t) => {
    const { mm, log } = make(t);
    mm.queue('duel');
    await flush();
    last().open();
    await flush();
    last().push(2011, ['node-b', 'node', 'queue']);
    await flush();
    assert.equal(FakeWs.all.length, 2);
    assert.match(last().url, /ticket=2&node=node-b/);
    last().open();
    await flush();
    assert.deepEqual(last().events, ['rejoin']);
    assert.equal(mm.socket?.node?.nodeId, 'node-b');
    last().push(2012, []);
    assert.equal(mm.socket?.node, null);
    assert.deepEqual(log, ['draining']);
  });

  it('leave in a match sends the leave frame; connect failures and lost sockets report errors', async (t) => {
    const { mm, log } = make(t);
    mm.queue('duel');
    await flush();
    last().open();
    await flush();
    last().push(2001, ['r']);
    const ws = last();
    mm.leave();
    assert.deepEqual(ws.events, ['queue', 'return']);
    assert.equal(mm.phase, 'idle');

    mm.queue('duel');
    await flush();
    last().fail();
    await flush();
    assert.equal(mm.phase, 'idle');

    mm.queue('duel');
    await flush();
    last().open();
    await flush();
    last().drop(4000);
    assert.equal(mm.phase, 'idle');
    assert.deepEqual(log, ['match:r:false', 'error:connect', 'error:lost']);
  });
});

describe('StatusPoller', () => {
  it('single-flight refresh, busy/idle periods, keeps the value on failure', async (t) => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let calls = 0;
    let fail = false;
    let now = 0;
    const p = new StatusPoller({
      fetch: async () => {
        calls++;
        if (fail) throw new Error('x');
        return { ban: 30 };
      },
      initial: { ban: 0 },
      isBusy: (s, age) => remainingSeconds(s.ban, age) > 0,
      now: () => now,
    });
    await Promise.all([p.refresh(), p.refresh()]);
    assert.equal(calls, 1);
    assert.equal(p.value.ban, 30);
    const seen: number[] = [];
    const off = p.subscribe((v) => seen.push(v.ban));
    assert.ok(p.busy);
    t.mock.timers.tick(5000);
    await flush();
    assert.equal(calls, 2);
    fail = true;
    now = 40_000;
    assert.equal(p.busy, false);
    p.set({ ban: 0 });
    t.mock.timers.tick(19_999);
    await flush();
    assert.equal(calls, 2);
    t.mock.timers.tick(1);
    await flush();
    assert.equal(calls, 3);
    assert.equal(p.value.ban, 0);
    off();
    t.mock.timers.tick(60_000);
    await flush();
    assert.equal(calls, 3);
    assert.deepEqual(seen, [30, 0]);
  });

  it('does nothing while disabled', async () => {
    let calls = 0;
    const p = new StatusPoller({ fetch: async () => ++calls, initial: 0, enabled: () => false });
    await p.refresh();
    assert.equal(calls, 0);
    assert.equal(remainingSeconds(5, 7), 0);
    p.stop();
  });
});
