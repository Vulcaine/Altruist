import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { Vec2Like } from '../src/math/vec2.ts';
import { Ballistics2D, BodyMotion2D, BodyState2D, ClosestRayHit2D, ContactNormals2D, Kinematics, rayCastClosest, Velocity2D } from '../src/physics/index.ts';
import type { RayCastWorld } from '../src/physics/index.ts';
import { FakeBody, inlineApproach, inlineClamp, inlineWrap, same, sameVec, units, vectors } from './helpers.ts';

const DT = 1 / 60;

function cases(): [Vec2Like, Vec2Like, number][] {
  const vs = vectors(1200);
  const us = units(1200);
  const out: [Vec2Like, Vec2Like, number][] = [];
  for (let i = 0; i < Math.min(vs.length, us.length); i++) out.push([vs[i]!, us[i]!, (i % 11) * 0.21 - 0.5]);
  return out;
}

describe('physics: Velocity2D', () => {
  it('matches the inline client forms', () => {
    for (const [v, n, k] of cases()) {
      const nx = n.x, ny = n.y;
      same(Velocity2D.approachSpeed(v, n), -(v.x * nx + v.y * ny));
      {
        let vx = v.x, vy = v.y;
        const into = vx * nx + vy * ny;
        if (into < 0) { vx -= nx * into; vy -= ny * into; }
        sameVec(Velocity2D.cancelInto(v, n), { x: vx, y: vy });
      }
      {
        let vx = v.x, vy = v.y;
        const along = vx * nx + vy * ny;
        if (along < 0) { vx -= nx * along * 0.8; vy -= ny * along * 0.8; }
        sameVec(Velocity2D.cancelInto(v, n, 0.8), { x: vx, y: vy });
      }
      {
        const along = v.x * nx + v.y * ny;
        const tx = (v.x - nx * along) * 0.6;
        const ty = (v.y - ny * along) * 0.6;
        sameVec(Velocity2D.bounce(v, n, 14, 0.6), { x: nx * 14 + tx, y: ny * 14 + ty });
        sameVec(Velocity2D.bounce(v, n, 14, 0.6), { x: tx + nx * 14, y: ty + ny * 14 });
      }
      {
        const rx = nx * 1.3, ry = ny * 1.3;
        sameVec(Velocity2D.pointVelocity(v, k, { x: rx, y: ry }), { x: v.x - k * ry, y: v.y + k * rx });
      }
      {
        let vx = v.x, vy = v.y;
        const stick = 30 + (vx * vx + vy * vy) / 12;
        vx -= nx * stick * DT;
        vy -= ny * stick * DT;
        sameVec(Velocity2D.accelerateAlong(v, n, -(30 + (v.x * v.x + v.y * v.y) / 12), DT), { x: vx, y: vy });
      }
      {
        const drag = 1 - 0.35 * DT;
        sameVec(Velocity2D.applyLinearDrag(v, 0.35, DT), { x: v.x * drag, y: v.y * drag });
        const g = 30 * DT;
        sameVec(Velocity2D.applyGravity(v, 30, DT, k), { x: v.x, y: v.y - g * k });
      }
      {
        const s = { x: v.x / 30, y: v.y };
        const dir = s.x !== 0 ? Math.sign(s.x) : -1;
        sameVec(Velocity2D.keepMinimumSpeedX(s, 1.5, -1), Math.abs(s.x) < 1.5 ? { x: dir * 1.5, y: s.y } : s);
      }
    }
    assert.deepEqual(Velocity2D.keepMinimumSpeedX({ x: 0, y: 3 }, 2, -1), { x: -2, y: 3 });
    assert.deepEqual(Velocity2D.bounce({ x: 4, y: -10 }, { x: 0, y: 1 }, 6, 0.5), { x: 2, y: 6 });
  });
});

describe('physics: Kinematics / Ballistics2D', () => {
  it('kinematics', () => {
    for (let i = 0; i < 2000; i++) {
      const dist = (i * 7.31) % 60, v0 = (i * 3.17) % 25;
      let t = 0;
      const tAcc = Math.max(0, (20 - v0) / 28);
      const dAcc = ((v0 + 20) / 2) * tAcc;
      if (dAcc >= dist) t += (-v0 + Math.sqrt(v0 * v0 + 2 * 28 * dist)) / 28;
      else t += tAcc + (dist - dAcc) / 20;
      same(Kinematics.timeToCover(dist, v0, 28, 20), t);
      same(Kinematics.stoppingDistance(v0, 40), (v0 * v0) / (2 * 40));
    }
    assert.equal(Kinematics.timeToCover(20, 0, 10, 10), 2.5);
    assert.equal(Kinematics.timeToCover(10, 30, 10, 10), 1);
  });
  it('closed forms', () => {
    for (const [v, n, k] of cases()) {
      const p = { x: n.x * 7, y: n.y * 7 };
      const t = Math.abs(k) + 0.25;
      sameVec(Ballistics2D.positionAt(p, v, 30, t), { x: p.x + v.x * t, y: p.y + v.y * t - 0.5 * 30 * t * t });
      same(Ballistics2D.apexHeight(v.y, 30), v.y > 0 ? (v.y * v.y) / (2 * 30) : 0);
      const lv = Ballistics2D.launchVelocity({ x: v.x + 3 - p.x, y: v.y - p.y }, 30, t);
      same(lv.y, (v.y - p.y + 0.5 * 30 * t * t) / t);
    }
    const v = Ballistics2D.launchVelocity({ x: 10, y: 3 }, 9.8, 1.5);
    const at = Ballistics2D.positionAt({ x: 0, y: 0 }, v, 9.8, 1.5);
    assert.ok(Math.abs(at.x - 10) < 1e-9 && Math.abs(at.y - 3) < 1e-9);
  });
  it('stepper matches the inline predictor with a floor bounce', () => {
    const g = 30, r = 1.2, e = 0.6, t = 1.37;
    const steps = Math.max(1, Math.round(t / 0.05));
    const h = t / steps;
    let x = -3, y = 8, vx = 11, vy = 4;
    for (let i = 0; i < steps; i++) {
      vy -= g * h;
      x += vx * h;
      y += vy * h;
      if (y < r && vy < 0) {
        y = r;
        vy = -vy * e;
      }
    }
    const s = Ballistics2D.predict({ x: -3, y: 8 }, { x: 11, y: 4 }, g, h, steps, r, e);
    sameVec(s.position, { x, y });
    sameVec(s.velocity, { x: vx, y: vy });
    const zone = { minX: 9, maxX: 11, minY: -100, maxY: 100 };
    assert.equal(Ballistics2D.firstStepWhere({ x: 0, y: 0 }, { x: 10, y: 0 }, 0, 0.1, 20, (p) => p.x >= zone.minX && p.x <= zone.maxX), 9);
    assert.equal(Ballistics2D.firstStepWhere({ x: 0, y: 0 }, { x: 10, y: 0 }, 0, 0.1, 5, (p) => p.x >= 9), -1);
  });
});

describe('physics: ContactNormals2D', () => {
  it('sums, classifies and normalizes like the inline ground update', () => {
    const up = { x: 0.2 / Math.hypot(0.2, 1), y: 1 / Math.hypot(0.2, 1) };
    const acc = new ContactNormals2D();
    let ax = 0, ay = 0, wx = 0, wy = 0, wheels = false;
    for (const n of units(40)) {
      acc.add(n, up, 0.5);
      ax += n.x;
      ay += n.y;
      if (n.x * up.x + n.y * up.y >= 0.5) { wheels = true; wx += n.x; wy += n.y; }
    }
    assert.equal(acc.hasSupport, wheels);
    const l = Math.sqrt(ax * ax + ay * ay) || 1;
    sameVec(acc.allDirection, { x: ax / l, y: ay / l });
    const lw = Math.sqrt(wx * wx + wy * wy) || 1;
    sameVec(acc.supportDirection, { x: wx / lw, y: wy / lw });
    const empty = new ContactNormals2D();
    assert.deepEqual(empty.allDirection, { x: 0, y: 0 });
    empty.add({ x: 1, y: 0 });
    assert.equal(empty.hasSupport, false);
  });
});

describe('physics: BodyMotion2D', () => {
  it('velocity ops write the math-layer results', () => {
    for (const [v, n, k] of cases().slice(0, 400)) {
      const b = new FakeBody({ v, w: k, a: k * 3, p: { x: n.x * 2, y: n.y * 2 } });
      same(BodyMotion2D.speedAlong(b, n), v.x * n.x + v.y * n.y);
      BodyMotion2D.addVelocityAlong(b, n, k);
      sameVec(b.v, { x: v.x + n.x * k, y: v.y + n.y * k });
      b.v = v;
      BodyMotion2D.clampSpeed(b, 20);
      const s = Math.sqrt(v.x * v.x + v.y * v.y);
      sameVec(b.v, s > 20 ? { x: (v.x / s) * 20, y: (v.y / s) * 20 } : v);
      b.v = v;
      BodyMotion2D.reduceSpeed(b, 0.25);
      sameVec(b.v, s === 0 ? v : { x: v.x - (v.x / s) * 0.25, y: v.y - (v.y / s) * 0.25 });
      b.v = v;
      b.w = k;
      const pre = { x: n.x * 4, y: n.y * 4 };
      BodyMotion2D.blendMotionFrom(b, pre, 1.5, 0.35);
      sameVec(b.v, { x: pre.x + (v.x - pre.x) * 0.35, y: pre.y + (v.y - pre.y) * 0.35 });
      same(b.w, 1.5 + (k - 1.5) * 0.35);
      b.v = v;
      b.w = k;
      const point = { x: n.x * 3 + 1, y: n.y * 3 };
      const rx = point.x - b.p.x, ry = point.y - b.p.y;
      sameVec(BodyMotion2D.velocityAtPoint(b, point), { x: v.x - k * ry, y: v.y + k * rx });
    }
  });
  it('angular ops match the inline forms', () => {
    for (const [v, n, k] of cases().slice(0, 800)) {
      const rot = v.x / 3;
      const b = new FakeBody({ a: rot, w: k * 4 });
      BodyMotion2D.alignUpToNormal(b, n, 12);
      same(b.w, inlineWrap(Math.atan2(-n.x, n.y) - rot) * 12);
      BodyMotion2D.arriveAtAngle(b, v.y, Math.abs(k) * 0.1, DT);
      same(b.w, inlineWrap(v.y - rot) / Math.max(Math.abs(k) * 0.1, DT));
      b.w = k * 4;
      BodyMotion2D.steerAngularVelocity(b, v.y, 10, 9, 60, DT);
      same(b.w, inlineApproach(k * 4, inlineClamp(inlineWrap(v.y - rot) * 10, -9, 9), 60 * DT));
      b.w = k * 4;
      BodyMotion2D.approachAngularVelocity(b, 0, 60, DT);
      same(b.w, inlineApproach(k * 4, 0, 60 * DT));
    }
  });
  it('writes only when needed; box queries; copy; state', () => {
    const b = new FakeBody({ v: { x: 5, y: 1 } });
    BodyMotion2D.keepMinimumSpeedX(b, 2, 1);
    BodyMotion2D.clampSpeed(b, 100);
    assert.equal(b.writes, 0);
    b.v = { x: 0, y: 1 };
    BodyMotion2D.keepMinimumSpeedX(b, 2, 1);
    assert.deepEqual(b.v, { x: 2, y: 1 });

    const box = new FakeBody({ p: { x: 10, y: 0 }, a: Math.PI / 2 });
    assert.ok(BodyMotion2D.boxContains(box, { x: 10, y: 1.9 }, 2, 0.5));
    assert.ok(!BodyMotion2D.boxContains(box, { x: 11.9, y: 0 }, 2, 0.5));
    assert.ok(BodyMotion2D.boxContains(box, { x: 11.9, y: 0 }, 2, 0.5, 0, 1.5));
    assert.ok(Math.abs(BodyMotion2D.boxDistanceSquared(box, { x: 10, y: 5 }, 2, 0.5) - 9) < 1e-9);

    const src = new FakeBody({ p: { x: 1, y: 2 }, v: { x: 3, y: 4 }, a: 0.5, w: 2 });
    const dst = new FakeBody();
    dst.awake = false;
    BodyMotion2D.copyMotionFrom(dst, src);
    assert.deepEqual([dst.p, dst.v, dst.a, dst.w, dst.awake], [src.p, src.v, 0.5, 2, true]);

    const saved = BodyState2D.capture(src);
    src.setTransform({ x: 9, y: 9 }, 3);
    src.setLinearVelocity({ x: 0, y: 0 });
    src.active = false;
    BodyState2D.restore(saved, src, true, true);
    assert.deepEqual([src.p, src.v, src.a, src.w, src.awake, src.active], [{ x: 1, y: 2 }, { x: 3, y: 4 }, 0.5, 2, true, true]);
  });
});

describe('physics: ClosestRayHit2D', () => {
  // A world of horizontal segments at given heights hit by vertical rays (planck-style callback).
  type Seg = { y: number; x0: number; x1: number; tag: string };
  function world(segs: Seg[]): RayCastWorld<Seg> {
    return {
      rayCast(from, to, cb) {
        for (const s of segs) {
          if (from.x < s.x0 || from.x > s.x1) continue;
          const f = (from.y - s.y) / (from.y - to.y);
          if (f < 0 || f > 1) continue;
          const r = cb(s, { x: from.x, y: s.y }, { x: 0, y: 1 }, f);
          if (r === 0) return;
        }
      },
    };
  }
  const w = world([
    { y: 0.5, x0: -50, x1: 50, tag: 'solid' },
    { y: 3, x0: -1, x1: 1, tag: 'glass' },
    { y: 2, x0: 3, x1: 5, tag: 'solid' },
  ]);
  it('keeps the nearest accepted hit across several rays', () => {
    const hit = new ClosestRayHit2D<Seg>(10, (s) => s.tag === 'solid');
    hit.cast(w, { x: 0, y: 10 }, { x: 0, y: 0 });
    assert.ok(hit.found);
    assert.equal(hit.point.y, 0.5);
    same(hit.distance, hit.fraction * 10);
    hit.cast(w, { x: 4, y: 10 }, { x: 4, y: 0 });
    assert.equal(hit.point.y, 2);
    assert.equal(hit.fixture?.y, 2);
    hit.reset();
    assert.equal(hit.found, false);
    assert.equal(hit.distance, Infinity);
    const any = rayCastClosest(w, { x: 0, y: 10 }, { x: 0, y: 0 });
    assert.equal(any.point.y, 3);
    assert.ok(Math.abs(any.distance - 7) < 1e-12);
    assert.equal(rayCastClosest(w, { x: -60, y: 10 }, { x: -60, y: 5 }).found, false);
    const rejected = new ClosestRayHit2D<Seg>(4, () => false);
    assert.equal(rejected.onHit(w as unknown as Seg, { x: 0, y: 0 }, { x: 0, y: 1 }, 0.5), -1);
  });
});
