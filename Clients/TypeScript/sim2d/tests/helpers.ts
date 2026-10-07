import assert from 'node:assert/strict';
import type { Vec2Like } from '../src/math/vec2.ts';

/** Same number, bit for bit (Object.is: distinguishes -0 and matches NaN). */
export function same(actual: number, expected: number, msg?: string): void {
  assert.ok(Object.is(actual, expected), msg ?? `got ${actual}, inline gave ${expected}`);
}

export function sameVec(actual: Vec2Like, expected: Vec2Like): void {
  same(actual.x, expected.x, `x: got ${actual.x}, inline gave ${expected.x}`);
  same(actual.y, expected.y, `y: got ${actual.y}, inline gave ${expected.y}`);
}

/** Small deterministic generator for test inputs (not the library one). */
function lcg(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

export const EDGES = [0, -0, 1, -1, 0.5, -0.5, Math.PI, -Math.PI, Math.PI / 2, 2 * Math.PI, -2 * Math.PI, 3 * Math.PI, 1e-7, -1e-7, 123.456, -98765.4];

export function floats(count = 2000, range = 50, seed = 1): number[] {
  const r = lcg(seed);
  const out = [...EDGES];
  for (let i = 0; i < count; i++) out.push((r() * 2 - 1) * range);
  return out;
}

export function vectors(count = 1500, range = 50, seed = 2): Vec2Like[] {
  const r = lcg(seed);
  const out: Vec2Like[] = [{ x: 0, y: 0 }, { x: -0, y: 0 }, { x: 1, y: 0 }, { x: 0, y: -1 }];
  for (let i = 0; i < count; i++) out.push({ x: (r() * 2 - 1) * range, y: (r() * 2 - 1) * range });
  return out;
}

export function units(count = 1500, seed = 3): Vec2Like[] {
  const r = lcg(seed);
  const out: Vec2Like[] = [{ x: 1, y: 0 }, { x: 0, y: 1 }, { x: 0, y: -1 }];
  for (let i = 0; i < count; i++) {
    const a = r() * 2 * Math.PI;
    out.push({ x: Math.cos(a), y: Math.sin(a) });
  }
  return out;
}

/** A minimal planck-like body for tests (planck's Body has the same methods). */
export class FakeBody {
  p: Vec2Like = { x: 0, y: 0 };
  v: Vec2Like = { x: 0, y: 0 };
  a = 0;
  w = 0;
  awake = true;
  active = true;
  writes = 0;
  constructor(init: Partial<{ p: Vec2Like; v: Vec2Like; a: number; w: number }> = {}) {
    Object.assign(this, init);
  }
  getPosition(): Vec2Like { return this.p; }
  getLinearVelocity(): Vec2Like { return this.v; }
  setLinearVelocity(v: Vec2Like): void { this.v = { x: v.x, y: v.y }; this.writes++; }
  getAngle(): number { return this.a; }
  getAngularVelocity(): number { return this.w; }
  setAngularVelocity(w: number): void { this.w = w; }
  setTransform(p: Vec2Like, a: number): void { this.p = { x: p.x, y: p.y }; this.a = a; }
  getWorldCenter(): Vec2Like { return this.p; }
  getLocalPoint(wp: Vec2Like): Vec2Like {
    const dx = wp.x - this.p.x, dy = wp.y - this.p.y, c = Math.cos(this.a), s = Math.sin(this.a);
    return { x: c * dx + s * dy, y: -s * dx + c * dy };
  }
  getWorldVector(l: Vec2Like): Vec2Like {
    const c = Math.cos(this.a), s = Math.sin(this.a);
    return { x: c * l.x - s * l.y, y: s * l.x + c * l.y };
  }
  isAwake(): boolean { return this.awake; }
  setAwake(f: boolean): void { this.awake = f; }
  isActive(): boolean { return this.active; }
  setActive(f: boolean): void { this.active = f; }
}

/** DriftLink-style inline forms (copied verbatim from typical client code). */
export function inlineWrap(a: number): number {
  a = (a + Math.PI) % (2 * Math.PI);
  if (a < 0) a += 2 * Math.PI;
  return a - Math.PI;
}

export function inlineApproach(current: number, target: number, maxDelta: number): number {
  if (current < target) return Math.min(current + maxDelta, target);
  return Math.max(current - maxDelta, target);
}

export function inlineClamp(v: number, min: number, max: number): number {
  return v < min ? min : v > max ? max : v;
}
