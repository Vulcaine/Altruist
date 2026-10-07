import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { Aabb2D, Angle, DeterministicRandom, Direction2D, Geometry2D, PiecewiseLinear, Polyline2D, Rotation2D, Scalar, VectorMath2D } from '../src/math/index.ts';
import type { Vec2Like } from '../src/math/vec2.ts';
import { floats, inlineApproach, inlineClamp, inlineWrap, same, sameVec, units, vectors } from './helpers.ts';

const DEG = Math.PI / 180;

describe('math: Scalar', () => {
  it('matches the inline forms', () => {
    const xs = floats();
    for (let i = 0; i + 2 < xs.length; i++) {
      const a = xs[i]!, b = xs[i + 1]!, c = xs[i + 2]!;
      same(Scalar.clamp(a, -1, 1), inlineClamp(a, -1, 1));
      same(Scalar.clamp01(a), inlineClamp(a, 0, 1));
      same(Scalar.approach(a, b, Math.abs(c)), inlineApproach(a, b, Math.abs(c)));
      same(Scalar.lerp(a, b, c), a + (b - a) * c);
      same(Scalar.inverseLerp(a, b, c), (c - a) / (b - a));
      same(Scalar.pow01(a, 1.7), Math.pow(inlineClamp(a, 0, 1), 1.7));
      same(Scalar.roundHalfUp(a), Math.round(a));
      same(Scalar.quantize(a / 40), Math.round(inlineClamp(a / 40, -1, 1) * 127) / 127);
    }
  });
  it('semantics', () => {
    assert.equal(Scalar.approach(9, 10, 3), 10);
    assert.equal(Scalar.approach(1, 0, 4), 0);
    assert.equal(Scalar.remap(5, 0, 10, 100, 200), 150);
    assert.equal(Scalar.sign(Number.NaN), 0);
    assert.equal(Scalar.signOr(0, -1), -1);
    assert.equal(Scalar.signOr(-3, 1), -1);
    assert.equal(Scalar.roundHalfUp(-2.5), -2);
    assert.equal(Scalar.quantize(2), 1);
    assert.equal(Scalar.clamp(5, 3, 1), 1);
  });
});

describe('math: Angle', () => {
  it('wrap matches the inline modulo wrap', () => {
    for (const a of floats(5000, 100)) same(Angle.wrap(a), inlineWrap(a));
    assert.equal(Angle.wrap(Math.PI), -Math.PI);
  });
  it('rate helpers match the inline forms', () => {
    const xs = floats(1500, 10);
    for (let i = 0; i + 1 < xs.length; i++) {
      const rot = xs[i]!, tgt = xs[i + 1]!;
      same(Angle.wrappedDelta(rot, tgt), inlineWrap(tgt - rot));
      same(Angle.nearestEquivalent(rot, tgt), rot + inlineWrap(tgt - rot));
      same(Angle.nearestFullTurn(rot), rot - inlineWrap(rot));
      same(Angle.rateToward(rot, tgt, 12), inlineWrap(tgt - rot) * 12);
      same(Angle.clampedRateToward(rot, tgt, 9, 7), inlineClamp(inlineWrap(tgt - rot) * 9, -7, 7));
      same(Angle.arriveRate(rot, tgt, 0.004, 1 / 60), inlineWrap(tgt - rot) / Math.max(0.004, 1 / 60));
    }
  });
  it('normalize keeps +π and moveToward never overshoots', () => {
    assert.equal(Angle.normalize(Math.PI), Math.PI);
    assert.ok(Math.abs(Angle.normalize(2 * Math.PI + 0.5) - 0.5) < 1e-12);
    assert.ok(Math.abs(Angle.normalize(-2 * Math.PI - 0.5) + 0.5) < 1e-12);
    assert.equal(Angle.moveToward(0, 1, 5), 1);
    assert.ok(Math.abs(Angle.moveToward(0, 1, 0.25) - 0.25) < 1e-12);
    assert.ok(Math.abs(Angle.toDegrees(Angle.toRadians(30)) - 30) < 1e-12);
  });
});

describe('math: PiecewiseLinear', () => {
  const table: [number, number][] = [[0, 0.1], [10, 0.4], [25, 0.8], [40, 1]];
  const rise: [number, number][] = [[0, 0], [0.167, 2.6], [0.333, 4.5], [0.5, 5.4], [0.6, 5.6]];
  it('evaluate / inverse ends', () => {
    assert.equal(PiecewiseLinear.evaluate(table, -5), 0.1);
    assert.equal(PiecewiseLinear.evaluate(table, 100), 1);
    assert.equal(PiecewiseLinear.evaluate([], 3), 0);
    assert.equal(PiecewiseLinear.inverse(rise, 0), 0);
    assert.equal(PiecewiseLinear.inverse(rise, 99), Infinity);
    assert.equal(PiecewiseLinear.inverse([], 1), Infinity);
    for (const x of floats(1000, 60)) {
      const t = PiecewiseLinear.inverse(rise, x / 8);
      if (x / 8 > 0 && x / 8 <= 5.6) assert.ok(Math.abs(PiecewiseLinear.evaluate(rise, t) - x / 8) < 1e-9);
    }
  });
});

describe('math: DeterministicRandom (golden values shared with the C# tests)', () => {
  const golden: [number, number, number[]][] = [
    [0, 625341585, [3777279546, 2342155435, 1692513196, 1525286, 4071472047]],
    [1, 3144846624, [2953441579, 1270223261, 3107675839, 2406887145, 533617724]],
    [42, 3495691163, [1439054348, 3262749359, 3568059673, 3722848478, 121729184]],
    [-7, 2302564536, [117853228, 519489229, 63946394, 2005669396, 1553169107]],
    [2147483647, 3297604318, [805728533, 984706791, 4208799601, 3109502440, 2208842045]],
  ];
  it('raw sequences', () => {
    for (const [seed, state, seq] of golden) {
      const r = new DeterministicRandom(seed);
      assert.equal(r.state, state);
      for (const e of seq) assert.equal(r.nextUint(), e);
    }
  });
  it('derived draws', () => {
    const r = new DeterministicRandom(42);
    assert.equal(r.next(), 0.3350559500977397);
    assert.equal(r.signed(), 0.5193360666744411);
    assert.equal(r.gaussian(), 0.45177824376150966);
    assert.equal(r.jitter(3), -3);
    assert.equal(r.chance(0.5), false);
  });
  it('zero-state fallback, state round trip, ranges', () => {
    assert.equal(new DeterministicRandom(1539571937).state, 0x1234567);
    const a = new DeterministicRandom(9);
    a.nextUint();
    const b = DeterministicRandom.fromState(a.state);
    for (let i = 0; i < 100; i++) assert.equal(b.nextUint(), a.nextUint());
    b.state = 0;
    assert.equal(b.state, 0x1234567);
    const r = new DeterministicRandom(5);
    for (let i = 0; i < 10000; i++) {
      const n = r.next();
      assert.ok(n >= 0 && n < 1);
      const j = r.jitter(2);
      assert.ok(j >= -2 && j <= 2);
    }
    const before = r.state;
    assert.equal(r.jitter(0), 0);
    assert.equal(r.state, before);
  });
});

describe('math: VectorMath2D / Direction2D / Rotation2D', () => {
  it('normalizeOrZero matches `len(x, y) || 1`', () => {
    for (const v of vectors()) {
      const l = Math.sqrt(v.x * v.x + v.y * v.y) || 1;
      sameVec(VectorMath2D.normalizeOrZero(v), { x: v.x / l, y: v.y / l });
      const w = VectorMath2D.normalizeOrZeroWithLength(v);
      same(w.length, Math.sqrt(v.x * v.x + v.y * v.y));
    }
    sameVec(VectorMath2D.normalizeOr({ x: 0, y: 0 }, { x: 0, y: 1 }), { x: 0, y: 1 });
  });
  it('component helpers match the inline tangent-frame code', () => {
    const vs = vectors();
    const us = units();
    for (let i = 0; i < Math.min(vs.length, us.length); i++) {
      const v = vs[i]!, n = us[i]!;
      let vx = v.x, vy = v.y;
      const nx = n.x, ny = n.y;
      const k = (i % 7) * 0.37 - 1;
      same(VectorMath2D.cross(v, n), vx * ny - vy * nx);
      sameVec(VectorMath2D.crossScalar(k, v), { x: -k * vy, y: k * vx });
      sameVec(VectorMath2D.addAlong(v, n, k), { x: vx + nx * k, y: vy + ny * k });
      const into = vx * nx + vy * ny;
      sameVec(VectorMath2D.withComponentAlong(v, n, 3), { x: vx + nx * (3 - into), y: vy + ny * (3 - into) });
      const next = inlineApproach(into, 20, 0.4);
      sameVec(VectorMath2D.approachComponentAlong(v, n, 20, 0.4), { x: vx + nx * (next - into), y: vy + ny * (next - into) });
      const speed = Math.sqrt(vx * vx + vy * vy);
      const s2 = vx * nx + vy * ny >= 0 ? speed : -speed;
      sameVec(VectorMath2D.redirectAlong(v, n), { x: nx * s2, y: ny * s2 });
      sameVec(VectorMath2D.clampLength(v, 20), speed > 20 ? { x: (vx / speed) * 20, y: (vy / speed) * 20 } : v);
      if (speed > 0) sameVec(VectorMath2D.shortenBy(v, speed, 0.7), { x: vx - (vx / speed) * 0.7, y: vy - (vy / speed) * 0.7 });
      sameVec(VectorMath2D.lerp(v, n, k), { x: vx + (nx - vx) * k, y: vy + (ny - vy) * k });
      vx = 0;
      vy = 0;
      void vx;
      void vy;
    }
  });
  it('directions and rotations', () => {
    sameVec(Direction2D.perpendicularClockwise({ x: 0, y: 1 }), { x: 1, y: -0 });
    for (const d of floats(300, 720)) sameVec(Direction2D.fromPolarDegrees(d), { x: Math.cos(d * DEG), y: Math.sin(d * DEG) });
    for (const u of units(500)) {
      let ux = u.x, uy = u.y;
      if (uy > 0.92) {
        uy = 0.92;
        ux = Math.sign(ux) * Math.sqrt(1 - uy * uy);
      } else if (uy < -0.3) {
        uy = -0.3;
        ux = Math.sign(ux) * Math.sqrt(1 - uy * uy);
      }
      sameVec(Direction2D.clampElevation(u, -0.3, 0.92), { x: ux, y: uy });
      same(Rotation2D.angleAligningUp(u), Math.atan2(-u.x, u.y));
      const up = Rotation2D.rotate(Rotation2D.angleAligningUp(u), { x: 0, y: 1 });
      assert.ok(Math.abs(up.x - u.x) < 1e-12 && Math.abs(up.y - u.y) < 1e-12);
      const back = Rotation2D.unrotate(0.7, Rotation2D.rotate(0.7, u));
      assert.ok(Math.abs(back.x - u.x) < 1e-12);
    }
    assert.deepEqual(Direction2D.between({ x: 1, y: 1 }, { x: 1, y: 1 }), { x: 0, y: 0 });
    assert.deepEqual(Direction2D.perpendicular({ x: 2, y: 5 }), { x: -5, y: 2 });
    assert.ok(Math.abs(Direction2D.towardAngle(Math.PI / 2).x - 1) < 1e-12);
  });
});

describe('math: Geometry2D / Aabb2D / Polyline2D', () => {
  it('box and ray tests match the inline forms', () => {
    const vs = vectors(1500, 4);
    const us = units();
    for (let i = 0; i < Math.min(vs.length, us.length); i++) {
      const local = vs[i]!;
      const hw = 1.1, hh = 0.45;
      assert.equal(Geometry2D.boxContainsLocal(local, hw + 0.3, hh + 0.4), !(Math.abs(local.x) > hw + 0.3 || Math.abs(local.y) > hh + 0.4));
      const dx = Math.max(0, Math.abs(local.x) - hw);
      const dy = Math.max(0, Math.abs(local.y) - hh);
      same(Geometry2D.boxDistanceSquaredLocal(local, hw, hh), dx * dx + dy * dy);
      same(Geometry2D.rotatedBoxHalfExtentY(hw, hh, local.x), hw * Math.abs(Math.sin(local.x)) + hh * Math.abs(Math.cos(local.x)));
      const d = us[i]!, o = vs[(i + 1) % vs.length]!, p = { x: local.x * 3, y: local.y * 3 };
      const r = Geometry2D.pointToRay(o, d, p);
      same(r.along, (p.x - o.x) * d.x + (p.y - o.y) * d.y);
      same(r.offset, Math.abs((p.x - o.x) * d.y - (p.y - o.y) * d.x));
    }
    assert.equal(Geometry2D.rotatedBoxHalfExtentX(2, 1, 0), 2);
  });
  it('aabb', () => {
    const box = { minX: -2, maxX: 4, minY: 1, maxY: 3 };
    assert.ok(Aabb2D.contains(box, -2, 1));
    assert.ok(!Aabb2D.contains(box, 4.01, 2));
    assert.ok(Aabb2D.contains(box, 4.01, 2, 0.5));
    assert.deepEqual(Aabb2D.center(box), { x: 1, y: 2 });
    assert.deepEqual(Aabb2D.fromCorners({ x: 4, y: 3 }, { x: -2, y: 1 }), box);
    assert.ok(Aabb2D.intersects(box, { minX: 4, maxX: 5, minY: 3, maxY: 9 }));
  });
  it('arc tessellation matches the inline form; duplicates removed', () => {
    for (const [cx, cy, r, a0, a1] of [[10, 4, 6, 270, 360], [-30.5, 2.25, 12, 180, 90], [0, 0, 1, 0, 3]] as const) {
      const got: Vec2Like[] = [];
      Polyline2D.appendArc(got, cx, cy, r, a0, a1, 7.5);
      const exp: Vec2Like[] = [];
      const steps = Math.max(2, Math.ceil(Math.abs(a1 - a0) / 7.5));
      for (let i = 0; i <= steps; i++) {
        const a = (a0 + ((a1 - a0) * i) / steps) * DEG;
        exp.push({ x: cx + r * Math.cos(a), y: cy + r * Math.sin(a) });
      }
      assert.equal(got.length, exp.length);
      got.forEach((p, i) => sameVec(p, exp[i]!));
    }
    const pts: Vec2Like[] = [{ x: -40, y: 0 }, { x: -40, y: 0 }];
    Polyline2D.appendArc(pts, -34, 6, 6, 180, 270, 7.5);
    pts.push({ x: 40, y: 30 }, { x: -40, y: 30 }, { x: -40, y: 0 });
    const out = Polyline2D.removeDuplicates(pts);
    assert.deepEqual(out[out.length - 1], { x: -40, y: 30 });
    assert.equal(out.length, pts.length - 2); // the duplicate and the closing point
    assert.deepEqual(Polyline2D.removeDuplicates(pts, 1e-10, false).at(-1), { x: -40, y: 0 });
    assert.deepEqual(Polyline2D.removeDuplicates([]), []);
  });
});
