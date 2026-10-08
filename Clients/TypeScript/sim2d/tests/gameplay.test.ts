import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { GameplayVerbs2D as G } from '../src/gameplay/index.ts';
import type { Vec2Like } from '../src/math/vec2.ts';
import { FakeBody, inlineApproach, inlineWrap, same, sameVec, units, vectors } from './helpers.ts';

const DT = 1 / 60;

function cases(): [Vec2Like, Vec2Like, number][] {
  const vs = vectors(800);
  const us = units(800);
  const out: [Vec2Like, Vec2Like, number][] = [];
  for (let i = 0; i < Math.min(vs.length, us.length); i++) out.push([vs[i]!, us[i]!, (i % 13) * 0.17 - 0.6]);
  return out;
}

describe('gameplay verbs match the hand-written client updates', () => {
  it('jumpAsAimed jumps along the up of the aimed rotation', () => {
    for (const [v, , k] of cases()) {
      const aim = k * 9;
      const up = { x: -Math.sin(aim), y: Math.cos(aim) };
      const expected = new FakeBody({ v });
      G.jumpOff(expected, up, 9.5);
      const b = new FakeBody({ v });
      G.jumpAsAimed(b, aim, 9.5);
      sameVec(b.v, expected.v);
    }
  });

  it('jumpOff / pushAlong / launchAlong / cancelMotionAgainst', () => {
    for (const [v, n, k] of cases()) {
      let vx = v.x, vy = v.y;
      const into = vx * n.x + vy * n.y;
      if (into < 0) { vx -= n.x * into; vy -= n.y * into; }
      vx += n.x * 11.5;
      vy += n.y * 11.5;
      const b = new FakeBody({ v });
      G.jumpOff(b, n, 11.5);
      sameVec(b.v, { x: vx, y: vy });

      b.v = v;
      G.pushAlong(b, n, k);
      sameVec(b.v, { x: v.x + n.x * k, y: v.y + n.y * k });

      G.launchAlong(b, n, 18);
      sameVec(b.v, { x: n.x * 18, y: n.y * 18 });

      let cx = v.x, cy = v.y;
      const along = cx * n.x + cy * n.y;
      if (along < 0) { cx -= n.x * along * 0.75; cy -= n.y * along * 0.75; }
      b.v = v;
      G.cancelMotionAgainst(b, n, 0.75);
      sameVec(b.v, { x: cx, y: cy });
    }
    const r = new FakeBody({ a: Math.PI / 2 });
    G.launchForward(r, { x: 1, y: 0 }, 5);
    assert.ok(Math.abs(r.v.x) < 1e-12 && Math.abs(r.v.y - 5) < 1e-12);
  });

  it('bounceOff (pad and recorded impact) and absorbImpact', () => {
    for (const [v, n, k] of cases()) {
      const along = v.x * n.x + v.y * n.y;
      const tx = (v.x - n.x * along) * 0.4;
      const ty = (v.y - n.y * along) * 0.4;
      const b = new FakeBody({ v });
      G.bounceOff(b, n, 16, 0.4);
      sameVec(b.v, { x: tx + n.x * 16, y: ty + n.y * 16 });
      b.v = { x: 1, y: 1 };
      G.bounceOff(b, n, 16, 0.4, v);
      sameVec(b.v, { x: n.x * 16 + tx, y: n.y * 16 + ty });

      const pre = { x: n.x * 6, y: n.y * 6 };
      b.v = v;
      b.w = k;
      G.absorbImpact(b, pre, -2, 0.2);
      sameVec(b.v, { x: pre.x + (v.x - pre.x) * 0.2, y: pre.y + (v.y - pre.y) * 0.2 });
      same(b.w, -2 + (k - -2) * 0.2);
    }
  });

  it('surface driving: driveAlong, followSurface, stickTo, setSpeedAlong, brakeAlong', () => {
    for (const [v, n, k] of cases()) {
      const nx = n.x, ny = n.y, tx = ny, ty = -nx;
      const t = { x: tx, y: ty };
      let vx = v.x, vy = v.y;
      const vt = vx * tx + vy * ty;
      const next = inlineApproach(vt, 20, 6 * 0.8 * DT);
      vx += tx * (next - vt);
      vy += ty * (next - vt);
      const b = new FakeBody({ v });
      G.driveAlong(b, t, 20, 6 * 0.8, DT);
      sameVec(b.v, { x: vx, y: vy });

      const speed = Math.sqrt(vx * vx + vy * vy);
      const s2 = vx * tx + vy * ty >= 0 ? speed : -speed;
      vx = tx * s2;
      vy = ty * s2;
      G.followSurface(b, t);
      sameVec(b.v, { x: vx, y: vy });

      const stick = 25 + (vx * vx + vy * vy) / 14;
      vx -= nx * stick * DT;
      vy -= ny * stick * DT;
      G.stickTo(b, n, 25 + (b.v.x * b.v.x + b.v.y * b.v.y) / 14, DT);
      sameVec(b.v, { x: vx, y: vy });

      let ex = v.x, ey = v.y;
      const into = ex * nx + ey * ny;
      ex += nx * (k - into);
      ey += ny * (k - into);
      b.v = v;
      G.setSpeedAlong(b, n, k);
      sameVec(b.v, { x: ex, y: ey });

      b.v = v;
      G.brakeAlong(b, t, 40, DT);
      const bt = v.x * tx + v.y * ty;
      const bn = inlineApproach(bt, 0, 40 * DT);
      sameVec(b.v, { x: v.x + tx * (bn - bt), y: v.y + ty * (bn - bt) });
    }
  });

  it('speed limit, drag, bleed, fall, keepRolling', () => {
    for (const [v, , k] of cases()) {
      const b = new FakeBody({ v });
      G.clampTopSpeed(b, 25);
      const s = Math.sqrt(v.x * v.x + v.y * v.y);
      sameVec(b.v, s > 25 ? { x: (v.x / s) * 25, y: (v.y / s) * 25 } : v);
      b.v = v;
      G.applyDrag(b, 0.3, DT);
      const drag = 1 - 0.3 * DT;
      sameVec(b.v, { x: v.x * drag, y: v.y * drag });
      b.v = v;
      G.bleedSpeed(b, 0.5);
      sameVec(b.v, s === 0 ? v : { x: v.x - (v.x / s) * 0.5, y: v.y - (v.y / s) * 0.5 });
      b.v = v;
      G.fall(b, 30, DT, k);
      const g = 30 * DT;
      sameVec(b.v, { x: v.x, y: v.y - g * k });
      const slow = { x: v.x / 30, y: v.y };
      b.v = slow;
      G.keepRolling(b, 1.5, -1);
      const dir = slow.x !== 0 ? Math.sign(slow.x) : -1;
      sameVec(b.v, Math.abs(slow.x) >= 1.5 ? slow : { x: dir * 1.5, y: slow.y });
    }
  });

  it('orientation verbs', () => {
    for (const [v, n, k] of cases()) {
      const rot = v.x / 4;
      const b = new FakeBody({ a: rot, w: k * 5, p: v });
      G.alignToSurface(b, n, 14);
      same(b.w, inlineWrap(Math.atan2(-n.x, n.y) - rot) * 14);
      G.turnToAngleIn(b, v.y, 0.12, DT);
      same(b.w, inlineWrap(v.y - rot) / Math.max(0.12, DT));
      G.holdAngle(b, v.y, 8);
      same(b.w, inlineWrap(v.y - rot) * 8);
      b.w = k * 5;
      G.aimAt(b, v.y, 10, 9, 70, DT);
      const raw = inlineWrap(v.y - rot) * 10;
      same(b.w, inlineApproach(k * 5, raw < -9 ? -9 : raw > 9 ? 9 : raw, 70 * DT));
      b.w = k * 5;
      G.stopSpinning(b, 70, DT);
      same(b.w, inlineApproach(k * 5, 0, 70 * DT));
      same(G.uprightAngleOn(b, n), rot + inlineWrap(Math.atan2(-n.x, n.y) - rot));
      same(G.levelAngle(b), rot - inlineWrap(rot));
      const drop = Math.abs(k) * 0.1;
      const flat = rot + inlineWrap(Math.atan2(-n.x, n.y) - rot);
      const p = { x: b.p.x - n.x * drop, y: b.p.y - n.y * drop };
      G.snapUprightOn(b, n, drop);
      same(b.a, flat);
      sameVec(b.p, p);
      assert.equal(b.w, 0);
    }
  });

  it('questions: prediction, reach, aim, boxes', () => {
    for (const [v, n, k] of cases()) {
      const b = new FakeBody({ p: { x: n.x * 5, y: n.y * 5 }, v });
      const t = Math.abs(k) + 0.1;
      sameVec(G.predictPosition(b, t, 30), { x: b.p.x + v.x * t, y: b.p.y + v.y * t - 0.5 * 30 * t * t });
      const maxUp = v.y > 0 ? (v.y * v.y) / (2 * 30) : 0;
      same(G.riseLeft(b, 30), maxUp);
      assert.equal(G.canReachHeight(b, k * 20, 30, 1), !(k * 20 > maxUp + 1));
      same(G.impactSpeedAgainst(b, n), -(v.x * n.x + v.y * n.y));
      same(G.brakingDistanceAlong(b, { x: 1, y: 0 }, 35), (v.x * v.x) / (2 * 35));
    }
    const ball = new FakeBody({ p: { x: 0, y: 5 }, v: { x: 10, y: 0 } });
    const goal = { minX: 8, maxX: 12, minY: 0, maxY: 6 };
    assert.ok(G.willEnter(ball, goal, 10, 1.2, 24));
    assert.ok(!G.willEnter(ball, goal, 10, 0.5, 24));
    assert.ok(!G.willEnter(ball, goal, 10, 1.2, 24, 0, 0, { x: -10, y: 0 }));
    const vel = G.velocityToHit(ball, { x: 12, y: 2 }, 9.8, 1.1);
    const lands = G.predictPosition(new FakeBody({ p: ball.p, v: vel }), 1.1, 9.8);
    assert.ok(Math.abs(lands.x - 12) < 1e-9 && Math.abs(lands.y - 2) < 1e-9);
    const car = new FakeBody({ v: { x: -5, y: 0 } });
    assert.equal(G.timeToReach(car, { x: 20, y: 0 }, 10, 10), 2.5);
    car.v = { x: 10, y: 0 };
    assert.equal(G.timeToReach(car, { x: 20, y: 0 }, 10, 10), 2);
    assert.ok(G.predictLanding(new FakeBody({ p: { x: 0, y: 5 } }), 10, 0.05, 40, 1, 0.5).position.y >= 1);
    const box = new FakeBody({ p: { x: 3, y: 0 } });
    assert.ok(G.isWithinBox(box, { x: 4.2, y: 0.6 }, 1, 0.5, 0.3, 0.2));
    assert.ok(!G.isWithinBox(box, { x: 4.2, y: 0.6 }, 1, 0.5));
    assert.ok(G.isClearOfBox(box, { x: 7, y: 0 }, 1, 0.5, 3));
    assert.ok(!G.isClearOfBox(box, { x: 6.9, y: 0 }, 1, 0.5, 3));
    const r = new FakeBody();
    r.awake = false;
    G.resetMotion(r, { x: 3, y: 4 }, 0.2, { x: 1, y: 2 }, 0.5);
    assert.deepEqual([r.p, r.a, r.v, r.w, r.awake], [{ x: 3, y: 4 }, 0.2, { x: 1, y: 2 }, 0.5, true]);
  });
});
