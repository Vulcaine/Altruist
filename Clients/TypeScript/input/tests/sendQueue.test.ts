import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { ButtonField, InputBuffer, InputPacer, InputSendQueue, quantize, quantizer, RoomInputModel, structuralEquals } from '../src/index.ts';

interface Pad {
  x: number;
  buttons: number;
}
const model = RoomInputModel.describe<Pad>().buttons((p) => p.buttons, (p, b) => ({ ...p, buttons: b })).action(1, 0).action(2, 1).build();

describe('InputSendQueue', () => {
  it('numbers inputs from 1, acks drop pending, packets carry the redundancy window', () => {
    const q = new InputSendQueue<number>({ redundancy: 3 });
    assert.equal(q.push(10), 1);
    q.push(11);
    q.push(12);
    q.push(13);
    assert.deepEqual(q.packet(), { lastSeq: 4, inputs: [11, 12, 13] });
    q.ack(2);
    assert.deepEqual(q.pending.map((p) => p.seq), [3, 4]);
    q.ack(1); // older ack: ignored
    assert.deepEqual(q.pending.map((p) => p.seq), [3, 4]);
    q.reset();
    assert.equal(q.push(1), 1);
  });
  it('keeps at most maxPending', () => {
    const q = new InputSendQueue<number>({ maxPending: 3 });
    for (let i = 0; i < 10; i++) q.push(i);
    assert.deepEqual(q.pending.map((p) => p.seq), [8, 9, 10]);
  });
  it('packets feed InputBuffer.offerRange: lost packets are recovered from redundancy', () => {
    const q = new InputSendQueue<Pad>({ redundancy: 3 });
    const buf = new InputBuffer<Pad>({ x: 0, buttons: 0 }, model);
    const sent: Pad[] = [];
    const applied: Pad[] = [];
    for (let i = 0; i < 30; i++) {
      const p = { x: 0, buttons: i % 4 === 1 ? 1 : i % 4 === 3 ? 2 : 0 };
      sent.push(p);
      q.push(p);
      const pk = q.packet();
      if (i % 3 !== 1) buf.offerRange(pk.lastSeq, pk.inputs); // every third packet lost
      if (i >= 2) applied.push(buf.consume()); // the server runs two steps behind
    }
    while (buf.depth > 0) applied.push(buf.consume());
    assert.deepEqual(applied, sent);
    assert.equal(buf.stats.pressesLost, 0);
  });
});

describe('InputPacer', () => {
  it('speeds up when the server queue is short and slows down when it is long, bounded by maxPace', () => {
    const p = new InputPacer();
    for (let i = 0; i < 50; i++) p.observe(0);
    assert.ok(p.pace > 1 && p.pace <= 1.05);
    const q = new InputPacer();
    for (let i = 0; i < 50; i++) q.observe(12);
    assert.ok(q.pace < 1 && q.pace >= 0.95);
  });
  it('matches the DriftLink arithmetic step by step', () => {
    const p = new InputPacer();
    let avg = 2, jit = 0, pace = 1;
    for (const [d, t] of [[3, 1], [0, 2], [5, undefined], [2, 4], [1, 0]] as [number, number | undefined][]) {
      jit += (Math.abs(d - avg) - jit) * 0.1;
      avg += (d - avg) * 0.1;
      const target = Math.min(5, Math.max(2, t ?? 0, 1 + 2 * jit));
      pace = 1 + Math.max(-0.05, Math.min(0.05, (target - avg) * 0.02));
      p.observe(d, t);
      assert.equal(p.pace, pace);
    }
    p.reset();
    assert.equal(p.pace, 1);
  });
});

describe('ButtonField and quantize', () => {
  it('reads and writes packed values, including the top bit', () => {
    const f = new ButtonField(6, 3);
    assert.equal(f.mask, 448);
    assert.equal(f.read(f.write(0xffff, 5)), 5);
    assert.equal(f.write(0, 9), f.write(0, 1));
    const top = new ButtonField(29, 3);
    assert.equal(top.read(top.write(0, 7)), 7);
    assert.throws(() => new ButtonField(30, 3));
  });
  it('quantizer quantizes named fields like Scalar.quantize', () => {
    const q = quantizer<Pad>(['x']);
    assert.deepEqual(q({ x: 0.123456, buttons: 3 }), { x: quantize(0.123456), buttons: 3 });
    assert.equal(q({ x: 2, buttons: 0 }).x, 1);
  });
  it('structuralEquals matches C# record equality (NaN equal, 0 == -0)', () => {
    assert.ok(structuralEquals({ a: NaN, b: 0 }, { a: NaN, b: -0 }));
    assert.ok(!structuralEquals({ a: 1 }, { a: 1, b: 2 }));
  });
});

describe('RoomInputModel declaration errors', () => {
  it('throws like the C# builder', () => {
    const b = () => RoomInputModel.describe<Pad>().buttons((p) => p.buttons, (p, v) => ({ ...p, buttons: v }));
    assert.throws(() => RoomInputModel.describe<Pad>().action(1, 0).build());
    assert.throws(() => b().action(1, 31).build(), RangeError);
    assert.throws(() => b().action(1, 0).action(3, 1).build());
  });
});
