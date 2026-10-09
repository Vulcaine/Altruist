import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { MenuGestures, RemoteClock } from '../src/index.ts';

describe('menu gestures', () => {
  it('a tap confirms', () => {
    const g = new MenuGestures();
    g.down(1, 100, 100, 0);
    assert.deepEqual(g.move(1, 104, 102), []);
    assert.deepEqual(g.up(1, 120), ['confirm']);
  });

  it('a drag steps the focus once per step distance, along its main axis', () => {
    const g = new MenuGestures({ stepDistance: 50 });
    g.down(1, 100, 100, 0);
    assert.deepEqual(g.move(1, 160, 110), ['right']);
    assert.deepEqual(g.move(1, 260, 110), ['right', 'right']);
    assert.deepEqual(g.move(1, 260, 170), ['down']);
    assert.deepEqual(g.up(1, 200), []);
  });

  it('screen up is up', () => {
    const g = new MenuGestures({ stepDistance: 50 });
    g.down(1, 100, 300, 0);
    assert.deepEqual(g.move(1, 100, 240), ['up']);
  });

  it('a two-finger tap goes back, once', () => {
    const g = new MenuGestures();
    g.down(1, 100, 100, 0);
    g.down(2, 200, 100, 10);
    assert.deepEqual(g.up(1, 80), []);
    assert.deepEqual(g.up(2, 90), ['back']);
  });

  it('a long press or a moved finger is no tap', () => {
    const g = new MenuGestures();
    g.down(1, 100, 100, 0);
    assert.deepEqual(g.up(1, 900), []);
    g.down(2, 100, 100, 1000);
    g.move(2, 130, 100);
    assert.deepEqual(g.up(2, 1100), []);
  });
});

describe('remote clock', () => {
  it('keeps the spacing of remote times under jitter', () => {
    const c = new RemoteClock();
    const a = c.toLocal(1000, 5030);
    const b = c.toLocal(1040, 5060);
    assert.equal(a, 5030);
    assert.equal(b, 5060);
    // A late message keeps its own time, 40 ms after the previous one.
    assert.equal(c.toLocal(1080, 5150), 5100);
  });

  it('never maps after the arrival and never goes back', () => {
    const c = new RemoteClock();
    c.toLocal(0, 100);
    // Arrived sooner than expected: the estimate moves; the time is the arrival at most.
    assert.equal(c.toLocal(10, 105), 105);
    assert.equal(c.toLocal(11, 104), 105);
  });
});
