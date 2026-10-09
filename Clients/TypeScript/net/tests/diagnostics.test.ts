import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  ClockSync,
  DelayLine,
  median,
  MemoryStorage,
  NETSIM_DEFAULTS,
  NetConditioner,
  netSimLabel,
  normalizeNetSim,
  PingMonitor,
  pingQuality,
  type ConditionerClock,
  type PingReading,
} from '../src/index.ts';

/** A manual clock: timers fire only when `advance` passes them. */
function manualClock(): ConditionerClock & { advance(ms: number): void; t: number } {
  let id = 0;
  const timers = new Map<number, { at: number; fn: () => void }>();
  const c = {
    t: 0,
    now: () => c.t,
    setTimeout: (fn: () => void, ms: number) => {
      timers.set(++id, { at: c.t + ms, fn });
      return id;
    },
    clearTimeout: (h: unknown) => void timers.delete(h as number),
    advance(ms: number) {
      const end = c.t + ms;
      for (;;) {
        const next = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
        if (!next || next[1].at > end) break;
        timers.delete(next[0]);
        c.t = Math.max(c.t, next[1].at);
        next[1].fn();
      }
      c.t = end;
    },
  };
  return c;
}

describe('ClockSync', () => {
  it('estimates the offset from the lowest-RTT sample', () => {
    const c = new ClockSync(3);
    assert.equal(c.ready, false);
    assert.equal(c.offset, 0);
    // server is 1000 ms ahead; symmetric 40 ms trip
    assert.ok(c.addSample(0, 1020, 40));
    // asymmetric slow trip: biased estimate, larger rtt → ignored
    c.addSample(100, 1200, 300);
    assert.equal(c.offset, 1000);
    assert.equal(c.rtt, 40);
    assert.equal(c.uncertainty, 20);
    assert.equal(c.toServerTime(500), 1500);
    assert.equal(c.toClientTime(1500), 500);
    c.addSample(400, 1450, 500);
    c.addSample(600, 1650, 700);
    // the 40 ms sample fell out of the window of 3
    assert.equal(c.rtt, 100);
    assert.equal(c.addSample(10, 0, 5), false);
    assert.equal(c.addSample(NaN, 0, 5), false);
    c.reset();
    assert.equal(c.ready, false);
  });
});

describe('ping quality', () => {
  it('buckets and medians', () => {
    assert.equal(pingQuality(null), 'unknown');
    assert.equal(pingQuality(70), 'good');
    assert.equal(pingQuality(71), 'ok');
    assert.equal(pingQuality(141), 'bad');
    assert.equal(pingQuality(30, 20, 40), 'ok');
    assert.equal(median([5, 1, 3]), 3);
    assert.equal(median([4, 1, 3, 2]), 2.5);
  });
});

describe('PingMonitor', () => {
  it('socket samples: median window, sim share, source switch', () => {
    let t = 0;
    const m = new PingMonitor({ now: () => t, window: 3 });
    const seen: PingReading[] = [];
    const off = m.subscribe((r) => seen.push(r));
    assert.equal(seen[0]!.quality, 'unknown');
    for (const [rtt, sim] of [[50, 0], [500, 0], [60, 10], [70, 20]] as const) {
      t += 100;
      m.reportSocket(rtt, sim);
    }
    m.reportSocket(-1);
    m.reportSocket(NaN);
    const r = m.reading;
    assert.equal(r.ms, 70); // median of 500, 60, 70
    assert.equal(r.simMs, 10);
    assert.equal(r.quality, 'good');
    assert.equal(r.source, 'socket');
    assert.equal(r.at, 400);
    off();
    off();
    assert.equal(seen.length, 5);
    m.dispose();
  });

  it('HTTP measure: ok, cold sample dropped, offline on failure, 429 ignored', async () => {
    let t = 0;
    let status = 200;
    let fail = false;
    const urls: string[] = [];
    const m = new PingMonitor({
      url: '/ping',
      now: () => t,
      simulatedRtt: () => 5,
      fetch: async (url) => {
        urls.push(url);
        t += 40;
        if (fail) throw new Error('down');
        return new Response(null, { status: status === 204 ? 204 : status });
      },
    });
    await m.measure();
    assert.equal(m.reading.ms, 45);
    assert.equal(m.reading.simMs, 5);
    assert.equal(m.reading.source, 'http');
    await m.measure();
    assert.equal(m.reading.ms, 45); // cold sample replaced, not averaged
    status = 429;
    await m.measure();
    assert.equal(m.reading.quality, 'good');
    status = 200;
    fail = true;
    await m.measure();
    assert.equal(m.reading.quality, 'offline');
    assert.equal(m.reading.ms, null);
    assert.deepEqual(urls, ['/ping?n=1', '/ping?n=2', '/ping?n=3', '/ping?n=4']);
    // a live socket keeps the reading online through HTTP failures
    m.reportSocket(80);
    await m.measure();
    assert.equal(m.reading.ms, 80);
    m.dispose();
  });

  it('paths: per-measurement targets, a fresh window per path, a failed primary retried on the next target', async () => {
    let t = 0;
    let primaryUp = true;
    let down = false;
    const urls: string[] = [];
    const m = new PingMonitor({
      now: () => t,
      target: () => (down ? { url: '/api/ping', path: 'fallback' } : { url: 'https://direct.example/api/ping', path: 'direct' }),
      onTargetFailed: (target) => {
        if (target.path !== 'direct') return false;
        down = true;
        return true;
      },
      fetch: async (url) => {
        urls.push(url);
        t += url.startsWith('https://direct') ? 20 : 60;
        if (url.startsWith('https://direct') && !primaryUp) throw new Error('blocked');
        return new Response(null, { status: 204 });
      },
    });
    await m.measure();
    await m.measure();
    assert.deepEqual([m.reading.ms, m.reading.path, m.reading.source], [20, 'direct', 'http']);
    primaryUp = false;
    await m.measure();
    assert.deepEqual(urls.slice(2), ['https://direct.example/api/ping?n=3', '/api/ping?n=4']);
    assert.deepEqual([m.reading.ms, m.reading.path, m.reading.quality], [60, 'fallback', 'good']);
    m.reportSocket(30, 0, 'fallback');
    assert.deepEqual([m.reading.ms, m.reading.path, m.reading.source], [30, 'fallback', 'socket']);
    m.reportSocket(10, 0, 'direct');
    assert.deepEqual([m.reading.ms, m.reading.path], [10, 'direct']);
    m.dispose();
  });

  it('paths: a failed target with nothing to fall back to goes offline after one attempt', async () => {
    let calls = 0;
    const m = new PingMonitor({
      target: () => ({ url: '/api/ping', path: 'origin' }),
      onTargetFailed: () => false,
      fetch: async () => {
        calls++;
        return new Response(null, { status: 503 });
      },
    });
    await m.measure();
    assert.equal(calls, 1);
    assert.equal(m.reading.quality, 'offline');
    m.dispose();
  });

  it('polls while a polling subscriber exists and stops after', async (t) => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let calls = 0;
    const m = new PingMonitor({
      now: () => 0,
      socketStaleMs: -1,
      fetch: async () => {
        calls++;
        return new Response(null, { status: 204 });
      },
    });
    const off = m.subscribe(() => {}, { poll: true });
    t.mock.timers.tick(0);
    await new Promise((r) => setImmediate(r));
    assert.equal(calls, 1);
    t.mock.timers.tick(600);
    await new Promise((r) => setImmediate(r));
    assert.equal(calls, 2);
    off();
    t.mock.timers.tick(10_000);
    await new Promise((r) => setImmediate(r));
    assert.equal(calls, 2);
    m.dispose();
  });
});

describe('DelayLine', () => {
  it('passes through synchronously when off', () => {
    const out: number[] = [];
    const l = new DelayLine<number>(() => null, (n, held) => out.push(n + held));
    l.push(1);
    assert.deepEqual(out, [1]);
    assert.equal(l.lastHeld, 0);
  });

  it('never reorders: a late item holds back the ones behind it', () => {
    const clock = manualClock();
    const delays = [100, 10, 10];
    const out: [number, number][] = [];
    const l = new DelayLine<number>(() => delays.shift() ?? 0, (n, held) => out.push([n, held]), clock);
    l.push(1);
    l.push(2);
    clock.advance(5);
    l.push(3);
    assert.equal(l.pending, 3);
    clock.advance(50);
    assert.deepEqual(out, []);
    clock.advance(50);
    assert.deepEqual(out, [
      [1, 100],
      [2, 100],
      [3, 95],
    ]);
  });

  it('flush releases at once in order; clear forgets', () => {
    const clock = manualClock();
    const out: number[] = [];
    const l = new DelayLine<number>(() => 1000, (n) => out.push(n), clock);
    l.push(1);
    l.push(2);
    l.flush();
    assert.deepEqual(out, [1, 2]);
    l.push(3);
    l.clear();
    clock.advance(5000);
    assert.deepEqual(out, [1, 2]);
  });
});

describe('NetConditioner', () => {
  it('normalizes, persists, gates and labels', () => {
    assert.deepEqual(normalizeNetSim({ enabled: true, rttMs: 9999, jitterMs: -5, lossPct: 2.345 }), { enabled: true, rttMs: 500, jitterMs: 0, lossPct: 2.3 });
    assert.deepEqual(normalizeNetSim('junk'), NETSIM_DEFAULTS);
    const storage = new MemoryStorage();
    const c = new NetConditioner({ storageKey: 'sim', storage, random: () => 0.5 });
    const changes: number[] = [];
    c.subscribe((s) => changes.push(s.rttMs));
    assert.equal(c.active, false);
    assert.equal(c.oneWay(), null);
    c.update({ enabled: true, rttMs: 200 });
    assert.equal(c.active, true);
    assert.equal(c.oneWay(), 100);
    assert.equal(c.sampleRtt(), 200);
    assert.equal(new NetConditioner({ storageKey: 'sim', storage }).settings.rttMs, 200);
    c.setAllowed(false);
    assert.equal(c.oneWay(), null);
    assert.deepEqual(changes, [200, 200]);
    assert.equal(netSimLabel({ enabled: true, rttMs: 150, jitterMs: 20, lossPct: 1 }), 'SIM +150 ±20 ms · 1% stalls');
  });

  it('jitter and stalls follow the random source', () => {
    const rolls = [1, 0]; // jitter +max, then a loss roll that hits
    const c = new NetConditioner({ random: () => rolls.shift() ?? 0.99 });
    c.update({ enabled: true, rttMs: 100, jitterMs: 40, lossPct: 5 });
    assert.equal(c.oneWay(), 50 + 20 + 200);
  });

  it('lines deliver through the conditioner clock', () => {
    const clock = manualClock();
    const c = new NetConditioner({ clock, random: () => 0.5 });
    c.update({ enabled: true, rttMs: 60 });
    const out: number[] = [];
    const line = c.line<number>((n) => out.push(n));
    line.push(7);
    clock.advance(29);
    assert.deepEqual(out, []);
    clock.advance(1);
    assert.deepEqual(out, [7]);
  });
});
