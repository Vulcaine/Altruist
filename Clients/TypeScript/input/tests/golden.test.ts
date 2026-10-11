/**
 * Cross-language golden vectors: the JSON files in ./golden are written by the C# test
 * `ClientTwinGoldenVectorTests` (Tests/Altruist/Gaming/Rooms) from the real RoomInputModel and
 * InputBuffer; the TypeScript twins must reproduce every value.
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { InputBuffer, RoomInputModel, type InputBufferOptions } from '../src/index.ts';

interface Pad {
  x: number;
  buttons: number;
}

const load = (name: string) => JSON.parse(readFileSync(new URL(`./golden/${name}`, import.meta.url), 'utf8'));

interface ModelDoc {
  name: string;
  buttons: boolean;
  starvedClearsAxis: boolean;
  actions: [number, number][];
  actionMask: number;
  order: number[];
  stageOf: number[];
  fitsOneStep: [number, number][];
  cases: (number | number[])[][];
}

function build(m: ModelDoc): RoomInputModel<Pad> {
  if (!m.buttons) return RoomInputModel.opaque<Pad>();
  const b = RoomInputModel.describe<Pad>().buttons((p) => p.buttons, (p, v) => ({ ...p, buttons: v }));
  for (const [mask, stage] of m.actions) b.action(mask, stage);
  if (m.starvedClearsAxis) b.whenStarved((p) => ({ ...p, x: 0 }));
  return b.build();
}

const pad = (x: number, buttons: number): Pad => ({ x, buttons });

describe('RoomInputModel golden vectors (C#)', () => {
  const doc = load('room-input-model.json') as { format: string; models: ModelDoc[] };
  it('has the expected format', () => assert.equal(doc.format, 'altruist.golden.room-input-model/1'));
  for (const m of doc.models) {
    it(`${m.name}: action mask, order, stages and fitsOneStep`, () => {
      const model = build(m);
      assert.equal(model.actionMask, m.actionMask);
      assert.deepEqual(model.inOrder(-1), m.order);
      assert.deepEqual([...model.order], m.order);
      for (let b = 0; b < 32; b++) assert.equal(model.stageOf(1 << b), m.stageOf[b], `stageOf bit ${b}`);
      for (const [mask, ok] of m.fitsOneStep) assert.equal(model.fitsOneStep(mask), ok === 1, `fitsOneStep ${mask}`);
    });
    it(`${m.name}: ${m.cases.length} presses / order / starved / merge cases`, () => {
      const model = build(m);
      for (const c of m.cases) {
        const [px, pb, ax, ab, bx, bb, pa, pbb, ordered, sx, sb, ok, mx, mb] = c as [number, number, number, number, number, number, number, number, number[], number, number, number, number, number];
        const prev = pad(px, pb);
        const a = pad(ax, ab);
        const b = pad(bx, bb);
        const ctx = JSON.stringify(c);
        assert.equal(model.presses(prev, a), pa, `presses(prev,a) ${ctx}`);
        assert.equal(model.presses(a, b), pbb, `presses(a,b) ${ctx}`);
        assert.deepEqual(model.inOrder(pa | pbb), ordered, `inOrder ${ctx}`);
        assert.deepEqual(model.starved(a), pad(sx, sb), `starved ${ctx}`);
        const merged = model.tryMerge(prev, a, b);
        assert.equal(merged !== undefined, ok === 1, `tryMerge ok ${ctx}`);
        if (merged !== undefined) assert.deepEqual(merged, pad(mx, mb), `merged ${ctx}`);
      }
    });
  }
});

type Op = [string, unknown[], unknown, number[]];

describe('InputBuffer golden vectors (C#)', () => {
  const doc = load('input-buffer.json') as {
    format: string;
    scenarios: { name: string; options: InputBufferOptions; ops: Op[]; stats: Record<string, number> }[];
  };
  const models = (load('room-input-model.json') as { models: ModelDoc[] }).models;
  const model = build(models.find((m) => m.name === 'three-stages')!);
  it('has the expected format', () => assert.equal(doc.format, 'altruist.golden.input-buffer/1'));
  for (const sc of doc.scenarios) {
    it(`${sc.name}: ${sc.ops.length} ops replay identically`, () => {
      const buf = new InputBuffer<Pad>(pad(0, 0), model, sc.options);
      sc.ops.forEach(([kind, args, result, state], i) => {
        const at = `${sc.name} op ${i} ${kind} ${JSON.stringify(args)}`;
        if (kind === 'offer') {
          const [seq, [x, b]] = args as [number, [number, number]];
          assert.equal(buf.offer(seq, pad(x, b)) ? 1 : 0, result, at);
        } else if (kind === 'range') {
          const [lastSeq, inputs] = args as [number, [number, number][]];
          assert.equal(buf.offerRange(lastSeq, inputs.map(([x, b]) => pad(x, b))), result, at);
        } else if (kind === 'consume') {
          const [x, b] = result as [number, number];
          assert.deepEqual(buf.consume(), pad(x, b), at);
        } else buf.reset();
        assert.deepEqual([buf.lastAppliedSeq, buf.lastQueuedSeq, buf.depth, buf.targetDepth, buf.jitter, buf.speculating], state, `${at} state`);
      });
      assert.deepEqual({ ...buf.stats }, sc.stats);
    });
  }
});
