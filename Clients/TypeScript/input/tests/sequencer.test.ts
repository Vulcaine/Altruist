import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  ButtonField,
  combineLargestAxes,
  FrameCounter,
  InputSequencer,
  PauseDetector,
  RoomInputModel,
  StateMerger,
  stepCutoff,
  type TimedInput,
} from '../src/index.ts';

/** A generic pad: two axes and a button word. Stage table like a typical action game. */
interface Pad {
  x: number;
  y: number;
  buttons: number;
}
const J = 1, D = 2, L = 4, R = 8, B = 16, AIM = 32;
/** A 3-bit direction index packed into bits 6..8 (held state, read only by the D press). */
const DIR = new ButtonField(6, 3);
const model = RoomInputModel.describe<Pad>()
  .buttons((p) => p.buttons, (p, b) => ({ ...p, buttons: b }))
  .action(J, 0)
  .action(B, 1)
  .action(L, 2)
  .action(R, 2)
  .action(D, 2)
  .build();
const NEUTRAL: Pad = { x: 0, y: 0, buttons: 0 };
const ORDER = [J, B, L, R, D];
const TICK = 1000 / 60;
const NAMES: Record<number, string> = { [J]: 'J', [D]: 'D', [L]: 'L', [R]: 'R', [B]: 'B' };
const names = (xs: number[]) => xs.map((b) => NAMES[b] ?? String(b));
const seqOf = (frames: Pad[]) => model.pressSequence(frames, NEUTRAL);
const allCanonical = (frames: Pad[]) => frames.every((f, i) => model.isCanonical(i === 0 ? NEUTRAL : frames[i - 1]!, f));

class Script {
  readonly seq = new InputSequencer(model, NEUTRAL);
  private state: Pad = NEUTRAL;
  readonly raw: Pad[] = [];

  at(t: number, c: Partial<Pad> & { press?: number; release?: number }): this {
    const s = { ...this.state };
    if (c.x !== undefined) s.x = c.x;
    if (c.y !== undefined) s.y = c.y;
    if (c.buttons !== undefined) s.buttons = c.buttons;
    if (c.press) s.buttons |= c.press;
    if (c.release) s.buttons &= ~c.release;
    this.state = s;
    this.raw.push(s);
    this.seq.push({ t, state: s });
    return this;
  }

  ticks(from: number, to: number): Pad[] {
    const out: Pad[] = [];
    for (let k = from; k <= to; k++) out.push(this.seq.next(k * TICK, k * TICK));
    return out;
  }
}

describe('InputSequencer: taps and holds', () => {
  it('a press and release 2 ms apart inside one step shows for exactly one step', () => {
    const f = new Script().at(5, { press: J }).at(7, { release: J }).ticks(1, 3);
    assert.deepEqual(f.map((x) => x.buttons), [J, 0, 0]);
  });
  it('a held button is one press', () => {
    const f = new Script().at(5, { press: D }).at(90, { release: D }).ticks(1, 7);
    assert.deepEqual(f.map((x) => x.buttons), [D, D, D, D, D, 0, 0]);
    assert.deepEqual(names(seqOf(f)), ['D']);
  });
  it('the same button tapped twice inside one step is two presses, a step apart', () => {
    const f = new Script().at(2, { press: J }).at(4, { release: J }).at(6, { press: J }).at(8, { release: J }).ticks(1, 4);
    assert.deepEqual(f.map((x) => x.buttons), [J, 0, J, 0]);
  });
});

describe('InputSequencer: order within a step', () => {
  it('L then R 3 ms apart (same stage): L this step, R the next', () => {
    const f = new Script().at(4, { press: L }).at(7, { press: R }).ticks(1, 3);
    assert.deepEqual(f.map((x) => x.buttons), [L, L | R, L | R]);
  });
  it('J then D share a step; D then J splits', () => {
    assert.deepEqual(new Script().at(3, { press: J }).at(5, { press: D }).ticks(1, 1)[0]!.buttons, J | D);
    assert.deepEqual(new Script().at(3, { press: D }).at(5, { press: J }).ticks(1, 2).map((x) => x.buttons), [D, D | J]);
  });
  it('an axis move after a press waits; before a press it shares the step', () => {
    assert.deepEqual(new Script().at(3, { press: J }).at(6, { x: 1 }).ticks(1, 2), [
      { x: 0, y: 0, buttons: J },
      { x: 1, y: 0, buttons: J },
    ]);
    assert.deepEqual(new Script().at(3, { x: 1 }).at(6, { press: J }).ticks(1, 1)[0], { x: 1, y: 0, buttons: J });
  });
  it('a release after a press waits for the next step', () => {
    const s = new Script().at(1, { press: B });
    s.ticks(1, 1);
    s.at(20, { press: L }).at(22, { release: B });
    assert.deepEqual(s.ticks(2, 3).map((x) => x.buttons), [B | L, L]);
  });
  it('several presses in one change split in stage order', () => {
    const f = new Script().at(3, { press: D | L | R | J | B }).ticks(1, 4);
    assert.deepEqual(names(seqOf(f)), ['J', 'B', 'L', 'R', 'D']);
    assert.ok(allCanonical(f));
  });
});

describe('InputSequencer: packed value fields and wide masks', () => {
  it('a direction value set with its press applies with it', () => {
    const f = new Script().at(3, { buttons: DIR.write(AIM | D, 5) }).ticks(1, 1);
    assert.equal(DIR.read(f[0]!.buttons), 5);
    assert.equal(f[0]!.buttons & D, D);
  });
  it('a value change after a press waits for the next step (even when it only sets bits)', () => {
    const s = new Script().at(3, { buttons: DIR.write(D, 1) }).at(5, { buttons: DIR.write(D, 3) });
    const f = s.ticks(1, 2);
    assert.deepEqual(f.map((x) => DIR.read(x.buttons)), [1, 3]);
  });
  it('a value change before a press shares its step', () => {
    const s = new Script().at(3, { buttons: DIR.write(0, 6) }).at(5, { buttons: DIR.write(D, 6) });
    assert.deepEqual(s.ticks(1, 1)[0]!.buttons, DIR.write(D, 6));
  });
  it('actions on bit 31 and bit 8 sequence like any other', () => {
    const HI = 1 << 31, MID = 1 << 8;
    const m = RoomInputModel.describe<Pad>().buttons((p) => p.buttons, (p, b) => ({ ...p, buttons: b })).action(HI, 0).action(MID, 0).build();
    const q = new InputSequencer(m, NEUTRAL);
    q.push({ t: 1, state: { x: 0, y: 0, buttons: HI | MID } });
    assert.equal(q.next(10).buttons, HI);
    assert.equal(q.next(20).buttons, HI | MID);
  });
});

describe('InputSequencer: random streams keep every press, in order, canonical', () => {
  const rng = (seed: number) => () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 2 ** 32);
  for (let seed = 1; seed <= 200; seed++) {
    it(`seed ${seed}`, () => {
      const r = rng(seed);
      const s = new Script();
      let t = 0;
      let buttons = 0;
      const n = 10 + Math.floor(r() * 40);
      for (let i = 0; i < n; i++) {
        t += r() < 0.8 ? r() * 6 : r() * 60;
        const roll = r();
        if (roll < 0.15) s.at(t, { x: [-1, 0, 1][Math.floor(r() * 3)]! });
        else if (roll < 0.2) s.at(t, { buttons: (buttons = DIR.write(buttons, Math.floor(r() * 8))) });
        else if (roll < 0.25) s.at(t, { buttons: (buttons = (Math.floor(r() * 32) & model.actionMask) | (buttons & DIR.mask)) });
        else s.at(t, { buttons: (buttons ^= ORDER[Math.floor(r() * ORDER.length)]!) });
      }
      const f = s.ticks(1, Math.ceil(t / TICK) + 2 * n + 4);
      assert.ok(allCanonical(f));
      assert.deepEqual(names(seqOf(f)), names(model.pressSequence(s.raw, NEUTRAL)));
      assert.deepEqual(f[f.length - 1], s.raw[s.raw.length - 1]);
      assert.equal(s.seq.pending, 0);
    });
  }
});

describe('InputSequencer: hitches, stale input, resync', () => {
  const st = (buttons: number, x = 0): Pad => ({ x, y: 0, buttons });
  it('catch-up steps with cutoffs each take their own slice', () => {
    const q = new InputSequencer(model, NEUTRAL);
    for (const [t, b] of [[10, J], [12, 0], [30, L], [31, 0], [45, R]] as const) q.push({ t, state: st(b) });
    assert.deepEqual([q.next(50, 16.7), q.next(50, 33.3), q.next(50)].map((x) => x.buttons), [J, L, R]);
  });
  it('changes older than staleMs only set the held state', () => {
    const q = new InputSequencer(model, NEUTRAL, { staleMs: 2000 });
    q.next(0);
    q.push({ t: 10, state: st(J) });
    q.push({ t: 11, state: st(0) });
    q.push({ t: 20, state: st(D, 1) });
    assert.deepEqual(q.next(2050), st(D, 1));
  });
  it('resync drops the queue; repeats are ignored; a too-long backlog resyncs', () => {
    const q = new InputSequencer(model, NEUTRAL, { maxQueue: 4 });
    q.push({ t: 1, state: st(L) });
    q.push({ t: 2, state: st(L) });
    assert.equal(q.pending, 1);
    q.resync();
    assert.deepEqual(q.next(5), st(L));
    for (let i = 0; i < 5; i++) q.push({ t: 10 + i, state: st(i % 2 ? J : 0) });
    assert.equal(q.pending, 0);
  });
  it('rejects a model without buttons', () => {
    assert.throws(() => new InputSequencer(RoomInputModel.opaque<Pad>(), NEUTRAL));
  });
  it('stepCutoff: catch-up steps end at their slice, the newest takes everything', () => {
    const dt = 1 / 60;
    assert.equal(stepCutoff(1000, 0.05 - dt, dt, 1, true), 1000 - (0.05 - dt) * 1000);
    assert.equal(stepCutoff(1000, 0.05 - 3 * dt, dt, 1, true), undefined);
    assert.equal(stepCutoff(1000, 0.05, dt, 1, false), undefined);
    assert.equal(stepCutoff(1000, 2 * dt, dt, 0.5, true), 1000 - 4 * dt * 1000);
  });
});

describe('StateMerger', () => {
  const merger = () => new StateMerger<Pad>(combineLargestAxes<Pad>(['x', 'y'], 'buttons', NEUTRAL), (a, b) => model.equals(a, b), NEUTRAL);
  const st = (t: number, buttons: number, x = 0): TimedInput<Pad> => ({ t, state: { x, y: 0, buttons } });
  it('interleaves devices in time order', () => {
    const out = merger().merge(
      [
        { id: 'kb', changes: [st(5, R)], current: { x: 0, y: 0, buttons: R } },
        { id: 'pad', changes: [st(3, L)], current: { x: 0, y: 0, buttons: L } },
      ],
      10,
    );
    assert.deepEqual(out.map((s) => s.state.buttons), [L, L | R]);
  });
  it('a button held on two devices is one press', () => {
    const out = merger().merge(
      [
        { id: 'kb', changes: [st(1, J), st(9, 0)], current: NEUTRAL },
        { id: 'pad', changes: [st(5, J)], current: { x: 0, y: 0, buttons: J } },
      ],
      10,
    );
    assert.deepEqual(out.map((s) => s.state.buttons), [J]);
  });
  it('larger axis wins; a device that disappears releases what it held; missed changes at now', () => {
    const m = merger();
    m.merge(
      [
        { id: 'kb', changes: [st(1, 0, 1)], current: { x: 1, y: 0, buttons: 0 } },
        { id: 'pad', changes: [st(2, D, -0.5)], current: { x: -0.5, y: 0, buttons: D } },
      ],
      3,
    );
    assert.deepEqual(m.merge([{ id: 'kb', changes: [], current: { x: 1, y: 0, buttons: 0 } }], 4), [st(4, 0, 1)]);
    assert.deepEqual(m.merge([{ id: 'kb', changes: [], current: { x: 1, y: 0, buttons: B } }], 7), [st(7, B, 1)]);
  });
});

describe('PauseDetector', () => {
  it('first sample and samples after more than pauseFrames frames resume', () => {
    const frames = new FrameCounter();
    const p = new PauseDetector(frames, 8);
    assert.equal(p.resumed(), true);
    for (let i = 0; i < 8; i++) frames.mark();
    assert.equal(p.resumed(), false);
    for (let i = 0; i < 9; i++) frames.mark();
    assert.equal(p.resumed(), true);
  });
});
