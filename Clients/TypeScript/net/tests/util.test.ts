import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
  Backoff,
  backoffDelay,
  Emitter,
  GateRateLimiter,
  JsonStore,
  MemoryStorage,
  SlidingWindowLimiter,
  TokenBucket,
  type StorageLike,
} from '../src/util/index.ts';

/** Shared with Tests/Altruist/Client/WireVectorTests.cs (the server's limiters give the same verdicts). */
const vectors = JSON.parse(readFileSync(new URL('../../../../Tests/Resources/WireVectors/wire-vectors.json', import.meta.url), 'utf8')) as {
  tokenBuckets: { name: string; capacity: number; perSecond: number; hits: { at: number; allow: boolean }[] }[];
  slidingWindows: { name: string; permit: number; windowSeconds: number; hits: { at: number; allowed: boolean; retryAfterSeconds: number }[] }[];
};

describe('Emitter', () => {
  type E = { a: [n: number]; b: [s: string, t: string] };

  it('calls listeners in order, supports once/off/clear and counts', () => {
    const e = new Emitter<E>();
    const seen: string[] = [];
    const off1 = e.on('a', (n) => seen.push(`1:${n}`));
    e.on('a', (n) => seen.push(`2:${n}`));
    e.once('b', (s, t) => seen.push(`${s}${t}`));
    e.emit('a', 1);
    e.emit('b', 'x', 'y');
    e.emit('b', 'x', 'y');
    off1();
    off1();
    e.emit('a', 2);
    assert.deepEqual(seen, ['1:1', '2:1', 'xy', '2:2']);
    assert.equal(e.listenerCount('a'), 1);
    e.clear();
    assert.equal(e.listenerCount('a'), 0);
  });

  it('isolates throwing listeners and skips listeners removed mid-emit', () => {
    const errors: unknown[] = [];
    const e = new Emitter<E>({ onError: (err) => errors.push(err) });
    const seen: number[] = [];
    let off2 = () => {};
    e.on('a', () => {
      off2();
      throw new Error('boom');
    });
    off2 = e.on('a', () => seen.push(2));
    e.on('a', (n) => seen.push(n));
    e.emit('a', 3);
    assert.deepEqual(seen, [3]);
    assert.equal(errors.length, 1);
  });

  it('a listener added during emit is not called for that emit', () => {
    const e = new Emitter<E>();
    let late = 0;
    e.on('a', () => e.on('a', () => late++));
    e.emit('a', 1);
    assert.equal(late, 0);
  });
});

describe('backoff', () => {
  it('doubles from 400 ms to a 4 s cap by default', () => {
    assert.deepEqual([0, 1, 2, 3, 4, 5].map((n) => backoffDelay(n)), [400, 800, 1600, 3200, 4000, 4000]);
  });

  it('applies deterministic jitter with an injected random', () => {
    assert.equal(backoffDelay(2, { jitter: 0.5, random: () => 0.5 }), 1200);
    assert.equal(backoffDelay(2, { jitter: 1, random: () => 0 }), 1600);
    assert.equal(backoffDelay(0, { baseMs: 100, factor: 3, maxMs: 1e9 }), 100);
    assert.equal(backoffDelay(2, { baseMs: 100, factor: 3, maxMs: 1e9 }), 900);
  });

  it('Backoff counts attempts and resets', () => {
    const b = new Backoff({ baseMs: 10, maxMs: 1000 });
    assert.deepEqual([b.next(), b.next(), b.next()], [10, 20, 40]);
    assert.equal(b.attempt, 3);
    b.reset();
    assert.equal(b.next(), 10);
  });
});

describe('TokenBucket (C# TokenBucketRateLimiter.Bucket twin)', () => {
  it('starts full, refills continuously, needs a whole token', () => {
    let t = 0;
    const b = new TokenBucket(3, 2, () => t);
    assert.deepEqual([b.tryTake(), b.tryTake(), b.tryTake(), b.tryTake()], [true, true, true, false]);
    t = 250; // +0.5 token
    assert.equal(b.tryTake(), false);
    t = 500; // +0.5 more → 1.0 (the failed take at 250 still refilled)
    assert.equal(b.tryTake(), true);
    assert.equal(b.waitMs(), 500);
    t = 10_000;
    assert.equal(b.available(), 3); // capped
  });

  for (const v of vectors.tokenBuckets) {
    it(`shared vector (C# TokenBucketRateLimiter): ${v.name}`, () => {
      let t = 0;
      const b = new TokenBucket(v.capacity, v.perSecond, () => t);
      v.hits.forEach((h, i) => {
        t = h.at * 1000;
        assert.equal(b.tryTake(), h.allow, `hit ${i} at ${h.at}s`);
      });
    });
  }

  it('zero capacity or rate never refills', () => {
    const b = new TokenBucket(0, 5, () => 0);
    assert.equal(b.tryTake(), false);
    assert.equal(b.waitMs(), Infinity);
    const c = new TokenBucket(1, 0, () => 0);
    assert.equal(c.tryTake(), true);
    assert.equal(c.waitMs(), Infinity);
    c.reset();
    assert.equal(c.tryTake(), true);
  });
});

describe('GateRateLimiter', () => {
  it('routes gates to buckets, strikes, then disconnects and drops', () => {
    let t = 0;
    const l = new GateRateLimiter(
      { strikesToDisconnect: 3, strikeWindowSeconds: 10, maxPayloadBytes: 100, oversizeStrikes: 2, buckets: { chat: { capacity: 1, perSecond: 1, gates: ['chat'] } }, defaultBucket: 'none' },
      () => t,
    );
    assert.equal(l.bucketOf('chat'), 'chat');
    assert.equal(l.bucketOf('move'), null);
    assert.equal(l.check('move', 10), 'allow');
    assert.equal(l.check('chat'), 'allow');
    assert.equal(l.check('chat'), 'drop');
    assert.equal(l.check('move', 101), 'disconnect'); // 1 + 2 oversize strikes
    assert.equal(l.check('move'), 'drop');
    l.reset();
    assert.equal(l.check('chat'), 'allow');
  });

  it('the strike window resets; default bucket and attribute gates apply', () => {
    let t = 0;
    const l = new GateRateLimiter(
      { strikesToDisconnect: 2, strikeWindowSeconds: 1, buckets: { default: { capacity: 1, perSecond: 0 }, slow: { capacity: 1, perSecond: 0 } }, gateAttributes: { emote: 'slow' } },
      () => t,
    );
    assert.equal(l.bucketOf('anything'), 'default');
    assert.equal(l.bucketOf('emote'), 'slow');
    assert.equal(l.check('a'), 'allow');
    assert.equal(l.check('a'), 'drop');
    t = 1500;
    assert.equal(l.check('a'), 'drop'); // window restarted: 1 strike
    assert.equal(l.check('a'), 'disconnect');
    assert.equal(l.waitMs('emote'), 0);
  });
});

describe('SlidingWindowLimiter (C# SlidingWindowCounter twin)', () => {
  it('permits N per window, rejects with whole-second Retry-After ≥ 1', () => {
    let t = 0;
    const l = new SlidingWindowLimiter(2, 60_000, () => t);
    assert.ok(l.hit('ip').allowed);
    t = 10_000;
    assert.ok(l.hit('ip').allowed);
    t = 20_000;
    const d = l.hit('ip');
    assert.equal(d.allowed, false);
    assert.equal(d.retryAfterSeconds, 40);
    assert.equal(d.retryAfterMs, 40_000);
    assert.ok(l.hit('other').allowed);
    t = 60_000; // the first hit expires exactly at the cutoff (<=)
    assert.ok(l.hit('ip').allowed);
    t = 69_999.5;
    assert.equal(l.hit('ip').retryAfterSeconds, 1);
    assert.equal(l.remaining('ip'), 0);
    l.reset('ip');
    assert.equal(l.remaining('ip'), 2);
  });

  for (const v of vectors.slidingWindows) {
    it(`shared vector (C# SlidingWindowCounter): ${v.name}`, () => {
      let t = 0;
      const l = new SlidingWindowLimiter(v.permit, v.windowSeconds * 1000, () => t);
      for (const h of v.hits) {
        t = h.at * 1000;
        const d = l.hit('k');
        assert.equal(d.allowed, h.allowed, `at ${h.at}s`);
        assert.equal(d.retryAfterSeconds, h.retryAfterSeconds, `at ${h.at}s`);
      }
    });
  }

  it('validates its arguments', () => {
    assert.throws(() => new SlidingWindowLimiter(0, 10), RangeError);
    assert.throws(() => new SlidingWindowLimiter(1, 0), RangeError);
  });
});

describe('JsonStore', () => {
  const opts = { key: 'k', version: 2, defaults: { music: 0.8, list: [1] } };

  it('loads defaults, saves and reloads, copies defaults', () => {
    const storage = new MemoryStorage();
    const s = new JsonStore({ ...opts, storage });
    const d = s.load();
    d.list.push(2);
    assert.deepEqual(s.load(), { music: 0.8, list: [1] });
    s.save({ music: 0.3, list: [] });
    assert.equal(storage.getItem('k'), '{"v":2,"data":{"music":0.3,"list":[]}}');
    assert.deepEqual(new JsonStore({ ...opts, storage }).load(), { music: 0.3, list: [] });
    s.clear();
    assert.deepEqual(s.load(), opts.defaults);
  });

  it('migrates older versions, discards newer / corrupt ones, normalizes', () => {
    const storage = new MemoryStorage();
    storage.setItem('k', '{"v":1,"data":{"volume":0.5}}');
    const migrate = (data: unknown, v: number) => (v === 1 ? { music: (data as { volume: number }).volume, list: [] } : undefined);
    assert.deepEqual(new JsonStore({ ...opts, storage, migrate }).load(), { music: 0.5, list: [] });
    assert.deepEqual(new JsonStore({ ...opts, storage }).load(), opts.defaults);
    storage.setItem('k', '{"v":3,"data":{}}');
    assert.deepEqual(new JsonStore({ ...opts, storage, migrate }).load(), opts.defaults);
    storage.setItem('k', 'not json');
    assert.deepEqual(new JsonStore({ ...opts, storage }).load(), opts.defaults);
    const clamp = new JsonStore({ ...opts, storage, normalize: (v: unknown) => ({ music: Math.min(1, (v as { music: number }).music), list: [] }) });
    assert.deepEqual(clamp.save({ music: 5, list: [9] }), { music: 1, list: [] });
  });

  it('falls back to memory when storage throws or is missing', () => {
    const broken: StorageLike = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('quota');
      },
      removeItem: () => {
        throw new Error('blocked');
      },
    };
    const s = new JsonStore({ ...opts, storage: broken });
    assert.deepEqual(s.load(), opts.defaults);
    s.save({ music: 0.1, list: [] });
    assert.ok(s.inMemory);
    assert.deepEqual(s.load(), { music: 0.1, list: [] });
    s.clear();
    const none = new JsonStore({ ...opts, storage: null });
    assert.ok(none.inMemory);
    none.save({ music: 0.2, list: [] });
    assert.equal(none.load().music, 0.2);
  });
});
