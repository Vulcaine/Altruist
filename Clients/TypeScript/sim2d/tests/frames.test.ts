import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import * as Aabb2D from '../src/math/aabb2D.ts';
import * as Angle from '../src/math/angle.ts';
import * as Distance2D from '../src/math/distance2D.ts';
import * as Direction2D from '../src/math/direction2D.ts';
import * as Geometry2D from '../src/math/geometry2D.ts';
import { NormalFrame2D } from '../src/math/normalFrame2D.ts';
import * as Rotation2D from '../src/math/rotation2D.ts';
import * as Scalar from '../src/math/scalar.ts';
import type { Vec2Like } from '../src/math/vec2.ts';
import * as VectorMath2D from '../src/math/vectorMath2D.ts';
import * as Ballistics2D from '../src/physics/ballistics2D.ts';
import * as BodyMotion2D from '../src/physics/bodyMotion2D.ts';
import { ContactImpact2D } from '../src/physics/contactImpact2D.ts';
import * as Kinematics from '../src/physics/kinematics.ts';
import * as Velocity2D from '../src/physics/velocity2D.ts';
import { FacingFrame2D } from '../src/gameplay/facingFrame2D.ts';
import * as G from '../src/gameplay/gameplayVerbs2D.ts';
import { FakeBody, floats, same, sameVec, units, vectors } from './helpers.ts';

/** FakeBody plus planck's `getLocalVector`. */
class FrameBody extends FakeBody {
  getLocalVector(w: Vec2Like): Vec2Like {
    const c = Math.cos(this.a), s = Math.sin(this.a);
    return { x: c * w.x + s * w.y, y: -s * w.x + c * w.y };
  }
}

function cases(): [Vec2Like, Vec2Like, number][] {
  const vs = vectors(1200);
  const us = units(1200);
  const out: [Vec2Like, Vec2Like, number][] = [];
  for (let i = 0; i < Math.min(vs.length, us.length); i++) out.push([vs[i]!, us[i]!, (i % 11) * 0.29 - 1.3]);
  for (const n of [{ x: 0, y: 1 }, { x: -0, y: 1 }, { x: 1, y: -0 }, { x: -1, y: 0 }])
    for (const v of [{ x: 0, y: 0 }, { x: -0, y: -0 }, { x: 3, y: -0 }, { x: -4, y: 5 }]) out.push([v, n, 0.5]);
  return out;
}

/** Copied from a game's hit-zone classifier. */
function inlineClassify(localX: number, localY: number, halfW: number, halfH: number, facing: number, axisBias: number): string {
  const nx = (localX * facing) / halfW;
  const ny = localY / halfH;
  if (Math.abs(nx) * axisBias >= Math.abs(ny)) return nx >= 0 ? 'front' : 'back';
  return ny >= 0 ? 'top' : 'bottom';
}

describe('NormalFrame2D matches the inline contact / surface forms', () => {
  it('left frame: tangent (-ny, nx), dots, compose', () => {
    for (const [v, n, k] of cases()) {
      const f = NormalFrame2D.left(n);
      const tx = -n.y, ty = n.x;
      sameVec(f.tangent, { x: tx, y: ty });
      same(f.along(v), v.x * n.x + v.y * n.y);
      same(f.across(v), v.x * tx + v.y * ty);
      same(f.into(v), -(v.x * n.x + v.y * n.y));
      const outN = v.x * 0.7 + k, outT = v.y * -0.3 - k;
      sameVec(f.compose(outN, outT), { x: n.x * outN + tx * outT, y: n.y * outN + ty * outT });
    }
  });

  it('right frame: surface tangent, |cross| speed, height, sign pick', () => {
    for (const [v, n, k] of cases()) {
      const f = NormalFrame2D.right(n);
      const nx = n.x, ny = n.y;
      sameVec(f.tangent, { x: ny, y: -nx });
      sameVec(f.tangent, Direction2D.perpendicularClockwise(n));
      same(f.across(v), v.x * ny + v.y * -nx);
      same(Math.abs(f.across(v)), Math.abs(v.x * ny - v.y * nx));
      const p = { x: v.x * 0.37 + k, y: v.y * 0.37 - k };
      same(f.heightOf(p, v), (p.x - v.x) * nx + (p.y - v.y) * ny);
      for (const facing of [1, -1]) {
        let tx = ny, ty = -nx;
        let side = Math.sign(v.x * tx + v.y * ty);
        if (side === 0) side = facing;
        tx *= side;
        ty *= side;
        sameVec(f.tangentToward(v, facing), { x: tx, y: ty });
        const s = ny * facing >= 0 ? 1 : -1;
        sameVec(f.tangentToward({ x: facing, y: 0 }, 1), { x: ny * s, y: -nx * s });
      }
    }
  });
});

describe('math additions match inline', () => {
  it('classifyBoxSide reproduces a facing hit-zone classifier', () => {
    let i = 0;
    for (const p of vectors(3000, 3)) {
      const bias = [1, 1.35, 0.7][i++ % 3]!;
      for (const facing of [1, -1]) {
        assert.equal(Geometry2D.classifyBoxSide({ x: p.x * facing, y: p.y }, 1.1, 0.42, bias), inlineClassify(p.x, p.y, 1.1, 0.42, facing, bias));
      }
    }
  });

  it('grow, weighted distance, rotateDegrees, clampMinMax, fullTurnRate', () => {
    const box = { minX: 52.5, maxX: 60, minY: 0, maxY: 9.25 };
    for (const p of vectors(2000, 70)) {
      assert.equal(
        Aabb2D.contains(Aabb2D.grow(box, 1, -0.3), p.x, p.y),
        p.x >= box.minX - 1 && p.x <= box.maxX + 1 && p.y >= box.minY + 0.3 && p.y <= box.maxY - 0.3,
      );
    }
    const vs = vectors(1500);
    for (let i = 1; i < vs.length; i++) {
      const a = vs[i]!, b = vs[i - 1]!;
      same(Distance2D.weightedBetween(a, b, 0.5), Math.abs(a.x - b.x) + Math.abs(a.y - b.y) * 0.5);
      same(Distance2D.weighted(a.x - b.x, a.y - b.y, 0.5), Math.abs(a.x - b.x) + Math.abs(a.y - b.y) * 0.5);
    }
    for (const rot of floats(150, 45)) {
      const { x: c, y: s } = Direction2D.fromPolarDegrees(rot);
      for (const v of vectors(100, 1)) sameVec(VectorMath2D.rotateDegrees(v, rot), { x: v.x * c - v.y * s, y: v.x * s + v.y * c });
    }
    for (const v of floats(2000, 30)) same(Scalar.clampMinMax(v, 0, 6.5), Math.min(Math.max(v, 0), 6.5));
    same(Scalar.clampMinMax(-0, 0, 5), 0);
    for (const t of [0.42, 0.5, 1 / 3]) for (const d of [1, -1]) same(Angle.fullTurnRate(t, d), (d * Math.PI * 2) / t);
  });

  it('sectors and angleAligningForward', () => {
    for (const a of [...floats(3000, 40), 1e300]) {
      same(Angle.sector(a, 8), Math.floor(((Math.atan2(Math.sin(a), Math.cos(a)) + Math.PI) / (2 * Math.PI)) * 8) % 8);
    }
    const vs = vectors(3000, 60);
    for (let i = 1; i < vs.length; i++) {
      const dx = vs[i]!.x, dy = vs[i - 1]!.y;
      for (const n of [8, 12]) same(Angle.sectorToward(dx, dy, n), Math.floor(((Math.atan2(dy, dx) + Math.PI) / (2 * Math.PI)) * n) % n);
    }
    assert.equal(Angle.sectorToward(-1, 0, 8), 0);
    for (const v of vectors(2000, 1))
      for (const facing of [1, -1]) {
        const noseLocal = facing > 0 ? 0 : Math.PI;
        same(Rotation2D.angleAligningForward(v, facing), Math.atan2(v.y, v.x) - noseLocal);
      }
  });
});

describe('ContactImpact2D and frame body ops match a designed hit written inline', () => {
  const vs = vectors(1500, 30, 11);
  const ps = vectors(1500, 4, 12);
  const us = units(1500, 13);
  const ws = floats(1500, 12, 14);

  it('members and response', () => {
    for (let i = 0; i < 1500; i++) {
      const p = ps[i]!, n = us[i]!, cv = vs[i]!, w = ws[i]!, c = ps[(i + 7) % ps.length]!, bv = vs[(i * 7 + 3) % vs.length]!;
      const imp = new ContactImpact2D(p, n, cv, w, c, bv);
      // Inline (a client's designed hit):
      const cp = Velocity2D.pointVelocity(cv, w, { x: p.x - c.x, y: p.y - c.y });
      const approachSpeed = (cp.x - bv.x) * n.x + (cp.y - bv.y) * n.y;
      const carInto = cp.x * n.x + cp.y * n.y;
      const tx = -n.y, ty = n.x;
      const ballT = bv.x * tx + bv.y * ty;
      const carT = cp.x * tx + cp.y * ty;
      sameVec(imp.strikerPointVelocity, cp);
      same(imp.closingSpeed, approachSpeed);
      same(imp.strikerInto, carInto);
      same(imp.targetSpeed, Math.sqrt(bv.x * bv.x + bv.y * bv.y));
      same(imp.targetTangential, ballT);
      same(imp.strikerTangential, carT);
      same(imp.slip, carT - ballT);

      const outN = approachSpeed * 1.4 * 1.2 + imp.targetSpeed * 0.3;
      const outT = ballT * (1 - 0.2) + carT * 0.2;
      const w0 = (i % 9) * 0.7 - 3;
      const ball = new FakeBody({ v: bv, w: w0 });
      BodyMotion2D.setVelocityInFrame(ball, imp.frame, imp.closingSpeed * 1.4 * 1.2 + imp.targetSpeed * 0.3, imp.targetTangential * (1 - 0.2) + imp.strikerTangential * 0.2);
      BodyMotion2D.addSpinFromSlip(ball, imp.slip, 1.4, 0.55);
      sameVec(ball.v, { x: n.x * outN + tx * outT, y: n.y * outN + ty * outT });
      same(ball.w, w0 + ((carT - ballT) / 1.4) * 0.55);
    }
  });

  it('capture, closingSpeed, centripetal', () => {
    const car = new FakeBody({ p: { x: 2, y: 1 }, v: { x: 9, y: -1 }, w: 2.5 });
    const ball = new FakeBody({ v: { x: -4, y: 0.5 } });
    const imp = ContactImpact2D.capture({ x: 3, y: 1.1 }, { x: 1, y: 0 }, car, ball);
    sameVec(imp.strikerVelocity, car.v);
    same(imp.strikerSpin, 2.5);
    for (let i = 1; i < Math.min(vs.length, us.length); i++) {
      const a = vs[i]!, b = vs[i - 1]!, nose = us[i]!;
      same(Velocity2D.closingSpeed(a, b, nose), (a.x - b.x) * nose.x + (a.y - b.y) * nose.y);
      same(Velocity2D.centripetalAcceleration(a, 12.5), (a.x * a.x + a.y * a.y) / 12.5);
      same(Kinematics.centripetalAcceleration(a.x, 7), (a.x * a.x) / 7);
    }
  });
});

describe('firstZoneEntered / predictZoneEntry match hand-written sweeps', () => {
  it('two goals and a floor', () => {
    const opp = { minX: 52, maxX: 58, minY: 0, maxY: 8 };
    const own = { minX: -58, maxX: -52, minY: 0, maxY: 8 };
    const R = 1.2, g = 27, h = 1 / 30;
    const zones = [Aabb2D.grow(opp, 1, -0.3), Aabb2D.grow(own, 1.5, 0.5)];
    const ps = vectors(2500, 50, 21);
    const vs = vectors(2500, 40, 22);
    let hits = 0;
    for (let c = 0; c < ps.length; c++) {
      const b = { x: ps[c]!.x, y: Math.abs(ps[c]!.y) * 0.3 + R };
      const v = vs[c]!;
      let expected = { zone: -1, step: -1 };
      let x = b.x, y = b.y, vy = v.y;
      for (let i = 0; i < 60; i++) {
        vy -= g * h;
        x += v.x * h;
        y += vy * h;
        if (y < R) break;
        if (x >= opp.minX - 1 && x <= opp.maxX + 1 && y >= opp.minY + 0.3 && y <= opp.maxY - 0.3) { expected = { zone: 0, step: i + 1 }; break; }
        if (x >= own.minX - 1.5 && x <= own.maxX + 1.5 && y >= own.minY - 0.5 && y <= own.maxY + 0.5) { expected = { zone: 1, step: i + 1 }; break; }
      }
      const got = Ballistics2D.firstZoneEntered(b, v, g, h, 60, zones, R);
      assert.deepEqual(got, expected);
      assert.deepEqual(G.predictZoneEntry(new FakeBody({ p: b, v }), zones, g, h, 60, R), expected);
      if (got.zone >= 0) hits++;
    }
    assert.ok(hits > 20);
  });
});

describe('FacingFrame2D and the axis verbs match inline', () => {
  const HW = 1.1, HH = 0.42;
  const ps = vectors(700, 20, 41);
  const qs = vectors(700, 3, 42);
  const as = floats(700, 7, 43);

  it('nose, up, tip, front, zones, underside, aim', () => {
    for (let i = 0; i < 700; i++) {
      const body = new FrameBody({ p: ps[i]!, a: as[i]!, v: { x: qs[(i + 1) % 700]!.x * 9, y: qs[(i + 1) % 700]!.y * 9 } });
      const facing = i % 2 === 0 ? 1 : -1;
      const f = new FacingFrame2D(body, facing, HW, HH);
      const p = { x: ps[i]!.x + qs[i]!.x, y: ps[i]!.y + qs[i]!.y };
      const nose = body.getWorldVector({ x: facing, y: 0 });
      const up = body.getWorldVector({ x: 0, y: 1 });
      sameVec(f.nose, nose);
      sameVec(f.up, up);
      const pa = body.getPosition();
      sameVec(f.noseTip, { x: pa.x + nose.x * HW, y: pa.y + nose.y * HW });
      assert.equal(f.isInFront(p), body.getLocalPoint(p).x * facing > 0);
      same(f.noseAlignment(body.v), nose.x * body.v.x + nose.y * body.v.y);
      const l = body.getLocalPoint(p);
      assert.equal(f.zoneAt(p, 1.35), inlineClassify(l.x, l.y, HW, HH, facing, 1.35));
      const ln = body.getLocalVector({ x: -qs[i]!.x, y: -qs[i]!.y });
      assert.equal(f.zoneToward({ x: -qs[i]!.x, y: -qs[i]!.y }, 1), inlineClassify(ln.x, ln.y, HW, HH, facing, 1));
      const ends = [-1, 1].map((end) => ({ x: pa.x + nose.x * HW * end - up.x * HH, y: pa.y + nose.y * HW * end - up.y * HH }));
      const u = f.undersideEnds();
      sameVec(u.back, ends[0]!);
      sameVec(u.front, ends[1]!);
      const noseLocal = facing > 0 ? 0 : Math.PI;
      same(f.aimRotationToward(p), Math.atan2(p.y, p.x) - noseLocal);
      same(G.heightAbove(body, p), body.getLocalPoint(p).y);
      sameVec(G.upDirection(body), up);
      const angle = body.a;
      G.flipHalfTurn(body);
      same(body.a, angle + Math.PI);
    }
  });

  it('setVelocityX / scaleVelocityY / clampVelocityY / scaleVelocityX', () => {
    for (const v of vectors(2000, 40)) {
      const dir = v.y > 0 ? 1 : -1;
      let b = new FakeBody({ v });
      G.setVelocityX(b, dir * 31.5);
      G.scaleVelocityY(b, 0.3);
      sameVec(b.v, { x: dir * 31.5, y: v.y * 0.3 });
      b = new FakeBody({ v });
      G.setVelocityX(b, dir * Math.max(0, v.x * dir));
      G.clampVelocityY(b, 0, 6.5);
      sameVec(b.v, { x: dir * Math.max(0, v.x * dir), y: Math.min(Math.max(v.y, 0), 6.5) });
      const factor = 1 - 0.8 * Math.min(1, Math.abs(v.x * 0.01)) ** 1.5;
      b = new FakeBody({ v });
      G.scaleVelocityX(b, factor);
      sameVec(b.v, { x: v.x * factor, y: v.y });
      b = new FakeBody({ v });
      G.setVelocityY(b, 2);
      sameVec(b.v, { x: v.x, y: 2 });
    }
  });
});
