import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { RecedingPlanner, type PlannerOps } from '../src/index.ts';

/**
 * A toy problem: a candidate is a number `x` (starting at tick `t0`), its program presses on its
 * first frame, and a rollout scores `-(x - target)^2`. Neighbours are x ± 1.
 */
interface Cand {
  x: number;
  t0: number;
}

function toy(target: number, noise = 0) {
  const log = { evaluated: [] as number[], draws: 0, replans: 0 };
  const ops: PlannerOps<Cand, number> = {
    start: (c) => c.t0,
    frames: () => [1, 0, 0, 0, 0, 0, 0, 0],
    pressed: (f) => f !== 0,
    evaluate: (c) => {
      log.evaluated.push(c.x);
      return -((c.x - target) ** 2);
    },
    noise: () => {
      log.draws++;
      return noise;
    },
    neighbours: (c) => [{ ...c, x: c.x - 1 }, { ...c, x: c.x + 1 }],
    onReplan: () => log.replans++,
  };
  return { ops, log };
}

const settings = { recheckTicks: 3, switchMargin: 0.5, refineRounds: 3 };

describe('RecedingPlanner', () => {
  it('runs the best candidate and refines around it', () => {
    const p = new RecedingPlanner<Cand, number>(settings);
    const { ops } = toy(7);
    p.restart(0, [0, 4, 8].map((x) => ({ x, t0: 0 })));
    for (let tick = 0; tick < 4; tick++) p.search(0, 10, ops);
    assert.equal(p.best?.cand.x, 7);
    assert.equal(p.plan?.cand.x, 7);
    assert.equal(p.refined, 3);
  });

  it('spends exactly the budget, one unit per evaluation and per due re-scoring', () => {
    const p = new RecedingPlanner<Cand, number>(settings);
    const { ops, log } = toy(7);
    // Programs starting at tick 8: none of them goes stale by then.
    p.restart(0, Array.from({ length: 10 }, (_, x) => ({ x, t0: 8 })));
    assert.equal(p.search(5, 4, ops), 0);
    assert.equal(log.evaluated.length, 4);
    assert.equal(p.pending, 6);
    // Three ticks later the plan is due again: one unit for it, the rest for the queue.
    const running = p.plan!.cand.x;
    p.search(8, 2, ops);
    assert.deepEqual(log.evaluated.slice(4), [running, 4]);
  });

  it('skips a candidate whose presses fell before now, without spending a unit or a draw', () => {
    const p = new RecedingPlanner<Cand, number>(settings);
    const { ops, log } = toy(7);
    p.restart(10, [{ x: 7, t0: 4 }, { x: 3, t0: 10 }]);
    assert.equal(p.search(10, 5, ops), 4);
    assert.deepEqual(log.evaluated, [3]);
    assert.equal(log.draws, 1);
  });

  it('switches plans only past the margin, keeping the commitment and reporting the replan', () => {
    const p = new RecedingPlanner<Cand, number>({ ...settings, switchMargin: 5 });
    const { ops, log } = toy(0);
    p.restart(0, [{ x: 3, t0: 0 }]);
    p.search(0, 1, ops);
    assert.equal(p.commit(0), true);
    assert.equal(p.commit(1), false);
    p.restart(0, [{ x: 2, t0: 0 }, { x: 0, t0: 0 }]);
    // One unit re-scores the running plan (a restart makes it due), two for the queue.
    p.search(0, 3, ops);
    // x = 2 scores -4 against -9: better by 5, not past the margin; x = 0 (0) is.
    assert.equal(p.plan?.cand.x, 0);
    assert.equal(p.plan?.committed, true);
    assert.equal(p.plan?.commitTick, 0);
    assert.equal(log.replans, 1);
  });

  it('is settled when the search is done, on a sure score, or after the wait', () => {
    const p = new RecedingPlanner<Cand, number>(settings);
    const { ops } = toy(7);
    p.restart(0, [{ x: 7, t0: 0 }, { x: 1, t0: 0 }]);
    p.search(0, 1, ops);
    assert.equal(p.settled(1, 10, 3), false);
    assert.equal(p.settled(1, -1, 3), true);
    assert.equal(p.settled(3, 10, 3), true);
  });

  it('slides a plan a tick later and re-scores it next time', () => {
    const p = new RecedingPlanner<Cand, number>(settings);
    const { ops, log } = toy(7);
    p.adopt({ x: 7, t0: 0 }, [1, 0, 0], 20, 0);
    assert.equal(p.refined, 3);
    assert.equal(p.pending, 0);
    p.slide();
    assert.equal(p.plan?.start, 21);
    p.search(21, 1, ops);
    assert.deepEqual(log.evaluated, [7]);
    assert.equal(p.drop()?.cand.x, 7);
    assert.equal(p.plan, null);
  });
});
