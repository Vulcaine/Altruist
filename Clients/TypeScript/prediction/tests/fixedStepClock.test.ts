import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { FixedStepClock, OverrunPolicy, parseOverrun, runFixedSteps } from '../src/index.ts';

interface ClockDoc {
  hz: number;
  maxFrameDelta: number;
  maxStepsPerFrame: number;
  overrun: string;
  dt: number;
  frames: [number, number, number, number, number][];
  totalSteps: number;
}

describe('FixedStepClock golden vectors (C#)', () => {
  const doc = JSON.parse(readFileSync(new URL('./golden/fixed-step-clock.json', import.meta.url), 'utf8')) as { format: string; clocks: ClockDoc[] };
  it('has the expected format', () => assert.equal(doc.format, 'altruist.golden.fixed-step-clock/1'));
  for (const c of doc.clocks) {
    it(`${c.hz} Hz, max ${c.maxFrameDelta} s, ${c.maxStepsPerFrame} steps, ${c.overrun}: ${c.frames.length} frames bit for bit`, () => {
      const clock = new FixedStepClock(c.hz, { maxFrameDelta: c.maxFrameDelta, maxStepsPerFrame: c.maxStepsPerFrame, overrun: parseOverrun(c.overrun) });
      assert.ok(Object.is(clock.dt, c.dt));
      c.frames.forEach(([frame, steps, clamped, overrun, acc], i) => {
        assert.equal(clock.advance(frame), steps, `frame ${i} steps`);
        assert.ok(Object.is(clock.lastClamped, clamped), `frame ${i} clamped ${clock.lastClamped} vs ${clamped}`);
        assert.equal(clock.lastOverrun ? 1 : 0, overrun, `frame ${i} overrun`);
        assert.ok(Object.is(clock.accumulator, acc), `frame ${i} accumulator ${clock.accumulator} vs ${acc}`);
      });
      assert.equal(clock.totalSteps, c.totalSteps);
    });
  }
});

describe('FixedStepClock', () => {
  it('validates like C#', () => {
    assert.throws(() => new FixedStepClock(0));
    assert.throws(() => new FixedStepClock(60, { maxFrameDelta: 0 }));
    assert.throws(() => new FixedStepClock(60, { maxStepsPerFrame: 0 }));
  });
  it('parseOverrun accepts the config spellings', () => {
    assert.equal(parseOverrun(undefined), OverrunPolicy.Drop);
    assert.equal(parseOverrun(' Slow_Motion '), OverrunPolicy.SlowMotion);
    assert.equal(parseOverrun('CARRY'), OverrunPolicy.Carry);
    assert.throws(() => parseOverrun('skip'));
  });
  it('scale stretches game time after the clamp; alpha is the render fraction', () => {
    const a = new FixedStepClock(60);
    const b = new FixedStepClock(60);
    let sa = 0, sb = 0;
    for (let i = 0; i < 600; i++) {
      sa += a.advance(1 / 60, 1.05);
      sb += b.advance(1 / 60);
    }
    assert.ok(sa > sb + 25 && sa < sb + 35);
    assert.ok(a.alpha >= 0 && a.alpha < 1);
  });
  it('double step precision counts exactly 1 / hz: one step per 1 / hz frame', () => {
    const float = new FixedStepClock(60);
    const double = new FixedStepClock(60, { stepPrecision: 'double' });
    assert.equal(double.dt, 1 / 60);
    assert.equal(float.dt, Math.fround(1 / 60));
    const steps = (c: FixedStepClock) => Array.from({ length: 5 }, () => c.advance(1 / 60));
    assert.deepEqual(steps(double), [1, 1, 1, 1, 1]);
    assert.deepEqual(steps(float), [0, 1, 1, 1, 1]);
  });
  it('runFixedSteps hands catch-up steps their own cutoffs and the newest step none', () => {
    const clock = new FixedStepClock(60, { maxStepsPerFrame: 6 });
    const cutoffs: (number | undefined)[] = [];
    const n = runFixedSteps(clock, 0.051, 1, 1000, (until) => cutoffs.push(until));
    assert.equal(n, 3);
    assert.equal(cutoffs[2], undefined);
    assert.ok(cutoffs[0]! < cutoffs[1]! && cutoffs[1]! < 1000);
    const capped: (number | undefined)[] = [];
    runFixedSteps(new FixedStepClock(60, { maxStepsPerFrame: 2 }), 0.2, 1, 1000, (until) => capped.push(until));
    assert.equal(capped[1], undefined);
  });
});
