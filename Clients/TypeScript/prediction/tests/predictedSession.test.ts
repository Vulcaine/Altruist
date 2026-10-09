/**
 * PredictedSession against a fake authoritative server: a deterministic 1D world, the server's
 * InputBuffer twin (@altruist/input), and a scripted link with latency, jitter, loss and
 * reordering in both directions.
 */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { InputBuffer, quantize, RoomInputModel, type InputPacket } from '../../input/src/index.ts';
import { CorrectionSmoother, decayOffset, InterpolationBuffer, lerp, lerpAngle, PredictedSession, type PredictionHooks } from '../src/index.ts';

const HZ = 60;
const DT = Math.fround(1 / HZ);
const BOOST = 1;

interface Input {
  a: number;
  buttons: number;
}
interface World {
  tick: number;
  x: number;
  v: number;
  held: number;
  /** A remote object the client predicts as moving at constant speed. */
  other: number;
}
interface Snap extends World {
  ackSeq: number;
  inputDepth: number;
  inputTarget: number;
  round: number;
}
interface Frame {
  x: number;
  other: number;
}

const model = RoomInputModel.describe<Input>().buttons((i) => i.buttons, (i, b) => ({ ...i, buttons: b })).action(BOOST, 0).build();

/** One deterministic step; returns the events (a boost press). */
function stepWorld(w: World, input: Input): string[] {
  const ev: string[] = [];
  if ((input.buttons & ~w.held & BOOST) !== 0) {
    w.v += 5;
    ev.push('boost');
  }
  w.held = input.buttons;
  w.v += input.a * DT;
  w.x += w.v * DT;
  w.other += 2 * DT;
  w.tick++;
  return ev;
}

const rng = (seed: number) => () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 2 ** 32);

interface LinkOptions {
  upLatency?: number;
  downLatency?: number;
  upJitter?: number;
  downJitter?: number;
  upLoss?: number;
  downLoss?: number;
  reorder?: boolean;
  redundancy?: number;
  frames?: number;
  seed?: number;
  /** Server-side surprises at given ticks. */
  push?: (tick: number, w: World) => 'teleport' | void;
}

function run(o: LinkOptions = {}) {
  const r = rng(o.seed ?? 1);
  const server: World = { tick: 0, x: 0, v: 0, held: 0, other: 0 };
  const buf = new InputBuffer<Input>({ a: 0, buttons: 0 }, model);
  let round = 0;
  let now = 0;
  const up: { at: number; order: number; p: InputPacket<Input> }[] = [];
  const down: { at: number; order: number; s: Snap }[] = [];
  let order = 0;
  let lastUp = 0;
  let lastDown = 0;
  let sampled = 0;
  let pressesSampled = 0;
  let prevSampled = 0;
  const delay = (latency: number, jitter: number, last: number) => {
    const at = now + latency + Math.floor(r() * (jitter + 1));
    return o.reorder ? at : Math.max(at, last);
  };
  const client: World = { tick: 0, x: 0, v: 0, held: 0, other: 0 };
  const appliedTicks: number[] = [];
  const hooks: PredictionHooks<World, Input, Snap, Frame, string> = {
    header: (s) => s,
    applySnapshot: (w, s) => Object.assign(w, { tick: s.tick, x: s.x, v: s.v, held: s.held, other: s.other }),
    step: stepWorld,
    capture: (w) => ({ x: w.x, other: w.other }),
    lerpFrame: (a, b, t) => ({ x: lerp(a.x, b.x, t), other: lerp(a.other, b.other, t) }),
    sampleInput: () => {
      sampled++;
      const buttons = sampled % 20 < 3 ? BOOST : 0;
      if (buttons & ~prevSampled) pressesSampled++;
      prevSampled = buttons;
      return { a: quantize(Math.sin(sampled / 10)), buttons };
    },
    send: (p) => {
      if (r() < (o.upLoss ?? 0)) return;
      const at = delay(o.upLatency ?? 3, o.upJitter ?? 0, lastUp);
      lastUp = Math.max(lastUp, at);
      up.push({ at, order: order++, p: { lastSeq: p.lastSeq, inputs: [...p.inputs] } });
    },
    isTeleport: (a, b) => a.round !== b.round,
    onSnapshot: (_prev, s) => appliedTicks.push(s.tick),
    bodies: (f) => [
      { id: 'me', pos: [f.x] },
      { id: 'other', pos: [f.other], rate: 7 },
    ],
    applyOffset: (f, id, off) => {
      if (id === 'me') f.x += off.pos[0]!;
      else f.other += off.pos[0]!;
    },
  };
  const session = new PredictedSession(client, hooks, { hz: HZ, inputs: { redundancy: o.redundancy ?? 1 }, now: () => now * (1000 / 60) });
  const corrections: number[] = [];
  const depths: number[] = [];
  const events: string[] = [];
  const offsets: number[] = [];
  const meOffsets: number[] = [];
  const frames = o.frames ?? 900;
  for (now = 0; now < frames; now++) {
    for (const m of up.filter((m) => m.at <= now).sort((a, b) => a.at - b.at || a.order - b.order)) {
      up.splice(up.indexOf(m), 1);
      buf.offerRange(m.p.lastSeq, m.p.inputs);
    }
    stepWorld(server, buf.consume());
    if (o.push?.(server.tick, server) === 'teleport') round++;
    depths.push(buf.depth);
    if (r() >= (o.downLoss ?? 0)) {
      const at = delay(o.downLatency ?? 3, o.downJitter ?? 0, lastDown);
      lastDown = Math.max(lastDown, at);
      down.push({ at, order: order++, s: { ...server, ackSeq: buf.lastAppliedSeq, inputDepth: buf.depth, inputTarget: buf.targetDepth, round } });
    }
    for (const m of down.filter((m) => m.at <= now).sort((a, b) => a.at - b.at || a.order - b.order)) {
      down.splice(down.indexOf(m), 1);
      session.receive(m.s);
    }
    const before = session.appliedCount;
    events.push(...session.update(1 / 60));
    corrections.push(session.appliedCount > before ? session.lastCorrection : 0);
    offsets.push(Math.abs(session.correction.get('other')?.pos[0] ?? 0));
    meOffsets.push(Math.abs(session.correction.get('me')?.pos[0] ?? 0));
  }
  return { session, buf, corrections, depths, events, appliedTicks, pressesSampled, offsets, meOffsets, server, client };
}

const tail = <T>(xs: T[], n: number) => xs.slice(xs.length - n);

describe('PredictedSession: prediction and reconciliation', () => {
  it('a steady link predicts exactly: no correction once the server queue is paced', () => {
    const { corrections, buf } = run();
    // Only the first snapshots (before the first input arrived) correct anything.
    assert.ok(corrections.slice(30).every((c) => c === 0), `corrections ${corrections.slice(30).filter((c) => c !== 0)}`);
    assert.equal(buf.stats.pressesLost, 0);
  });

  it('pacing keeps the server queue near its target (never starving after warmup)', () => {
    const { depths, session, buf } = run({ upJitter: 2 });
    const late = tail(depths, 300);
    assert.ok(Math.min(...late) >= 1, `min depth ${Math.min(...late)}`);
    assert.ok(Math.max(...late) <= 6, `max depth ${Math.max(...late)}`);
    assert.ok(session.pace > 0.94 && session.pace < 1.06);
    assert.equal(buf.stats.pressesLost, 0);
  });

  it('input packet loss is recovered by redundancy', () => {
    const { corrections, buf } = run({ upLoss: 0.15, redundancy: 4, seed: 3 });
    assert.ok(tail(corrections, 400).every((c) => c === 0));
    assert.equal(buf.stats.pressesLost, 0);
  });

  it('lost, late and reordered snapshots: only newer ones are applied, prediction stays exact', () => {
    const { corrections, appliedTicks } = run({ downLoss: 0.2, downJitter: 6, reorder: true, seed: 5 });
    for (let i = 1; i < appliedTicks.length; i++) assert.ok(appliedTicks[i]! > appliedTicks[i - 1]!);
    assert.ok(tail(corrections, 400).every((c) => c === 0));
  });

  it('forward-step events are reported once; replayed steps report nothing', () => {
    const { events, pressesSampled } = run();
    assert.equal(events.length, pressesSampled);
  });

  it('an unpredicted server change glides out (decaying offset), a teleport snaps', () => {
    const glide = run({ push: (tick, w) => void (tick === 500 && (w.other += 0.5)) });
    const peak = Math.max(...glide.offsets);
    assert.ok(peak > 0.4 && peak <= 0.5, `peak offset ${peak}`);
    const at = glide.offsets.indexOf(peak);
    assert.ok(glide.offsets[at + 60]! < peak * 0.01, 'decayed within a second at rate 7');

    const jump = run({
      push: (tick, w) => {
        if (tick !== 500) return;
        w.x += 100;
        return 'teleport';
      },
    });
    const big = jump.corrections.findIndex((c) => c >= 99);
    assert.ok(big > 0, 'the teleport was a correction of 100');
    assert.equal(jump.meOffsets[big], 0, 'and it snapped (no offset)');
    assert.equal(jump.offsets[big], 0);
  });

  it('frame() interpolates and adds offsets; reset restarts sequences', () => {
    const { session } = run({ frames: 120 });
    const f = session.frame();
    assert.ok(Number.isFinite(f.x));
    session.reset();
    assert.equal(session.inputs.lastSeq, 0);
    assert.equal(session.lastApplied, null);
  });

  it('ignores a snapshot older than the one applied', () => {
    const w: World = { tick: 0, x: 0, v: 0, held: 0, other: 0 };
    const applied: number[] = [];
    const s = new PredictedSession<World, Input, Snap, Frame>(
      w,
      {
        header: (x) => x,
        applySnapshot: (sim, x) => void (sim.tick = x.tick),
        step: (sim) => void sim.tick++,
        capture: (sim) => ({ x: sim.tick, other: 0 }),
        lerpFrame: (a) => a,
        sampleInput: () => ({ a: 0, buttons: 0 }),
        send: () => {},
        onSnapshot: (_p, x) => void applied.push(x.tick),
      },
      { hz: 60, now: () => 0 },
    );
    const snap = (tick: number): Snap => ({ tick, x: 0, v: 0, held: 0, other: 0, ackSeq: 0, inputDepth: 2, inputTarget: 2, round: 0 });
    s.receive(snap(10));
    s.receive(snap(8));
    s.update(0);
    s.receive(snap(9));
    s.receive(snap(10));
    s.update(0);
    assert.deepEqual(applied, [10]);
  });
});

describe('Correction and interpolation helpers', () => {
  it('decayOffset is exponential without a speed cap and capped with one', () => {
    const o = [3, 4];
    decayOffset(o, 10, 0.1);
    assert.ok(Math.abs(Math.hypot(o[0]!, o[1]!) - 5 * Math.exp(-1)) < 1e-12);
    const c = [10, 0];
    decayOffset(c, 100, 0.01, 12, 4);
    assert.ok(Math.abs(c[0]! - (10 - Math.max(0.12, 10 * (1 - Math.exp(-0.04))))) < 1e-12);
  });
  it('CorrectionSmoother wraps angles, clamps offsets and drops gone objects', () => {
    const s = new CorrectionSmoother({ maxOffset: 1 });
    s.correct([{ id: 1, pos: [5, 0], angle: Math.PI - 0.1 }], [{ id: 1, pos: [0, 0], angle: -Math.PI + 0.1 }], false);
    const o = s.get(1)!;
    assert.deepEqual(o.pos, [1, 0]);
    assert.ok(Math.abs(o.angle - -0.2) < 1e-12);
    s.correct([], [], false);
    assert.equal(s.get(1), undefined);
  });
  it('InterpolationBuffer samples between states, clamps at the ends, accepts reordering', () => {
    const b = new InterpolationBuffer<number>({ capacity: 4 });
    b.push(2, 20);
    b.push(0, 0);
    b.push(1, 10);
    const l = (a: number, c: number, t: number) => lerp(a, c, t);
    assert.equal(b.sample(0.5, l), 5);
    assert.equal(b.sample(1.5, l), 15);
    assert.equal(b.sample(-1, l), 0);
    assert.equal(b.sample(5, l), 20);
    const e = new InterpolationBuffer<number>({ maxExtrapolation: 0.5 });
    e.push(0, 0);
    e.push(1, 10);
    assert.equal(e.sample(3, l), 15);
    assert.ok(Math.abs(lerpAngle(Math.PI - 0.1, -Math.PI + 0.1, 0.5) - Math.PI) < 1e-12);
  });
});
