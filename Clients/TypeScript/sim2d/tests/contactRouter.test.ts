import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { Vec2Like } from '../src/math/vec2.ts';
import { ContactRouter2D, touchingContacts, touchingContactsOf } from '../src/physics/index.ts';
import { same } from './helpers.ts';

type Surface = { kind: 'surface'; oneWay: boolean };
type Vehicle = { kind: 'vehicle'; id: number };
type Ball = { kind: 'ball' };
const isSurface = (u: unknown): u is Surface => (u as Surface | undefined)?.kind === 'surface';
const isVehicle = (u: unknown): u is Vehicle => (u as Vehicle | undefined)?.kind === 'vehicle';
const isBall = (u: unknown): u is Ball => (u as Ball | undefined)?.kind === 'ball';

/** planck-shaped fakes: world and body contact lists both link a new contact first. */
class FBody {
  ud: unknown;
  y: number;
  edges: FEdge | null = null;
  constructor(ud: unknown, y: number) {
    this.ud = ud;
    this.y = y;
  }
  getUserData(): unknown { return this.ud; }
  getContactList(): FEdge | null { return this.edges; }
}
class FFixture {
  ud: unknown;
  body: FBody;
  constructor(ud: unknown, body: FBody) {
    this.ud = ud;
    this.body = body;
  }
  getUserData(): unknown { return this.ud; }
  getBody(): FBody { return this.body; }
}
interface FEdge { contact: FContact; next: FEdge | null }
class FContact {
  fa: FFixture;
  fb: FFixture;
  touching: boolean;
  enabled = true;
  restitution = 0.5;
  wm: { normal: Vec2Like; points: Vec2Like[]; pointCount: number };
  next: FContact | null = null;
  constructor(fa: FFixture, fb: FFixture, touching: boolean, wm: { normal: Vec2Like; points: Vec2Like[]; pointCount: number }) {
    this.fa = fa;
    this.fb = fb;
    this.touching = touching;
    this.wm = wm;
  }
  getFixtureA(): FFixture { return this.fa; }
  getFixtureB(): FFixture { return this.fb; }
  isTouching(): boolean { return this.touching; }
  isEnabled(): boolean { return this.enabled; }
  setEnabled(f: boolean): void { this.enabled = f; }
  setRestitution(r: number): void { this.restitution = r; }
  setFriction(): void {}
  getWorldManifold(): { normal: Vec2Like; points: Vec2Like[]; pointCount: number } { return this.wm; }
  getNext(): FContact | null { return this.next; }
}
class FWorld {
  list: FContact | null = null;
  getContactList(): FContact | null { return this.list; }
  add(c: FContact): void {
    c.next = this.list;
    this.list = c;
    for (const b of new Set([c.fa.body, c.fb.body])) b.edges = { contact: c, next: b.edges };
  }
}

function lcg(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

/** A random arena: tags on fixtures or bodies, contacts with 0..2 points. */
function scene(seed: number) {
  const r = lcg(seed);
  const world = new FWorld();
  const vehicles = [0, 1, 2, 3, 4].map((id) => new FBody({ kind: 'vehicle', id }, r() * 6));
  const tagged = (ud: unknown, body: FBody) => (r() < 0.5 ? new FFixture(ud, body) : ((body.ud = body.ud ?? ud), new FFixture(undefined, body)));
  const surfaces = [0, 1, 2, 3].map((i) => new FBody(undefined, i * 1.5));
  const ball = new FBody({ kind: 'ball' }, 2);
  const fixtures: FFixture[] = [
    ...vehicles.map((v) => new FFixture(undefined, v)),
    ...surfaces.map((s, i) => tagged({ kind: 'surface', oneWay: i % 2 === 1 }, s)),
    new FFixture(undefined, ball),
    new FFixture(undefined, new FBody(undefined, 0)), // untagged
  ];
  for (let k = 0; k < 120; k++) {
    const a = fixtures[Math.floor(r() * fixtures.length)]!;
    const b = fixtures[Math.floor(r() * fixtures.length)]!;
    if (a.body === b.body) continue;
    const n = Math.floor(r() * 3);
    const ang = r() * 2 * Math.PI;
    const points = Array.from({ length: n }, () => ({ x: r() * 20 - 10, y: r() * 5 }));
    world.add(new FContact(a, b, r() < 0.85, { normal: { x: Math.cos(ang), y: Math.sin(ang) }, points, pointCount: n }));
    if (r() < 0.1) world.list!.enabled = false;
  }
  return { world, vehicles };
}

const passBall = (s: Surface, platform: FBody, ball: FBody) => s.oneWay && ball.y < platform.y + 0.5;
const passCar = (s: Surface, platform: FBody, car: FBody) => s.oneWay && car.y < platform.y;
const tag = (f: FFixture) => (f.getUserData() ?? f.getBody().getUserData()) as Surface | Vehicle | Ball | undefined;

/** contacts.ts onPreSolve, inline. */
function inlinePreSolve(contact: FContact, log: string[]): void {
  const fa = contact.getFixtureA();
  const fb = contact.getFixtureB();
  const ua = tag(fa);
  const ub = tag(fb);
  if (!ua || !ub) return;
  if (ua.kind === 'surface' && ub.kind === 'ball' && passBall(ua, fa.getBody(), fb.getBody())) return void (contact.setEnabled(false), log.push('ball-pass'));
  if (ub.kind === 'surface' && ua.kind === 'ball' && passBall(ub, fb.getBody(), fa.getBody())) return void (contact.setEnabled(false), log.push('ball-pass'));
  if ((ua.kind === 'surface' && ub.kind === 'vehicle' && passCar(ua, fa.getBody(), fb.getBody())) ||
    (ub.kind === 'surface' && ua.kind === 'vehicle' && passCar(ub, fb.getBody(), fa.getBody()))) {
    contact.setEnabled(false);
    log.push('car-pass');
    return;
  }
  const wm = contact.getWorldManifold();
  if (!wm || wm.pointCount === 0) return;
  let px = 0;
  let py = 0;
  for (let i = 0; i < wm.pointCount; i++) {
    px += wm.points[i]!.x;
    py += wm.points[i]!.y;
  }
  px /= wm.pointCount;
  py /= wm.pointCount;
  const nAx = wm.normal.x;
  const nAy = wm.normal.y;
  if (ua.kind === 'surface' && ub.kind === 'vehicle') log.push(`surface:${ub.id}:${px}:${py}:${nAx}:${nAy}`);
  else if (ub.kind === 'surface' && ua.kind === 'vehicle') log.push(`surface:${ua.id}:${px}:${py}:${-nAx}:${-nAy}`);
  else if (ua.kind === 'vehicle' && ub.kind === 'ball') {
    contact.setRestitution(0);
    log.push(`ball:${ua.id}:${px}:${py}:${nAx}:${nAy}`);
  } else if (ub.kind === 'vehicle' && ua.kind === 'ball') {
    contact.setRestitution(0);
    log.push(`ball:${ub.id}:${px}:${py}:${-nAx}:${-nAy}`);
  } else if (ua.kind === 'surface' && ub.kind === 'ball') log.push(`ball-surface:${px}:${py}:${nAx}:${nAy}`);
  else if (ub.kind === 'surface' && ua.kind === 'ball') log.push(`ball-surface:${px}:${py}:${-nAx}:${-nAy}`);
}

function routerLogging(log: string[]): ContactRouter2D<FContact> {
  return new ContactRouter2D<FContact>()
    .filterPreSolve(isSurface, isBall, (c) => (passBall(c.a, c.bodyA, c.bodyB) ? (log.push('ball-pass'), false) : true))
    .filterPreSolve(isSurface, isVehicle, (c) => (passCar(c.a, c.bodyA, c.bodyB) ? (log.push('car-pass'), false) : true))
    .onPreSolve(isSurface, isVehicle, (c) => log.push(`surface:${c.b.id}:${c.point.x}:${c.point.y}:${c.normal.x}:${c.normal.y}`))
    .onPreSolve(isVehicle, isBall, (c) => {
      c.setRestitution(0);
      log.push(`ball:${c.a.id}:${c.point.x}:${c.point.y}:${c.normal.x}:${c.normal.y}`);
    })
    .onPreSolve(isSurface, isBall, (c) => log.push(`ball-surface:${c.point.x}:${c.point.y}:${c.normal.x}:${c.normal.y}`));
}

describe('physics: ContactRouter2D', () => {
  it('reproduces the contacts.ts pre-solve ladder (log, enabled flags, restitution)', () => {
    for (let seed = 1; seed <= 40; seed++) {
      const a = scene(seed);
      const b = scene(seed);
      const inlineLog: string[] = [];
      const routerLog: string[] = [];
      const router = routerLogging(routerLog);
      for (let c = a.world.list, d = b.world.list; c && d; c = c.next, d = d.next) {
        inlinePreSolve(c, inlineLog);
        router.preSolve(d);
        assert.equal(d.enabled, c.enabled);
        assert.equal(d.restitution, c.restitution);
      }
      assert.deepEqual(routerLog, inlineLog);
      if (seed === 1) assert.ok(inlineLog.length > 10);
    }
  });

  it('touching queries reproduce the world walk and the per-body (updateGround) walk', () => {
    for (let seed = 1; seed <= 40; seed++) {
      const { world, vehicles } = scene(seed);
      // Server-style: walk the world list, flip to "out of the surface".
      const inline = new Map<number, { x: number; y: number; n: number }>();
      for (let c = world.list; c; c = c.next) {
        if (!c.isTouching() || !c.isEnabled()) continue;
        const ua = tag(c.fa);
        const ub = tag(c.fb);
        let id: number;
        let carIsA: boolean;
        if (ua?.kind === 'vehicle' && ub?.kind === 'surface') [id, carIsA] = [ua.id, true];
        else if (ub?.kind === 'vehicle' && ua?.kind === 'surface') [id, carIsA] = [ub.id, false];
        else continue;
        if (c.wm.pointCount === 0) continue;
        const acc = inline.get(id) ?? { x: 0, y: 0, n: 0 };
        acc.x += carIsA ? -c.wm.normal.x : c.wm.normal.x;
        acc.y += carIsA ? -c.wm.normal.y : c.wm.normal.y;
        acc.n++;
        inline.set(id, acc);
      }
      const routed = new Map<number, { x: number; y: number; n: number }>();
      touchingContacts(world, isSurface, isVehicle, (c) => {
        const acc = routed.get(c.b.id) ?? { x: 0, y: 0, n: 0 };
        acc.x += c.normal.x;
        acc.y += c.normal.y;
        acc.n++;
        routed.set(c.b.id, acc);
      });
      assert.deepEqual([...routed.keys()].sort(), [...inline.keys()].sort());
      for (const [id, acc] of inline) {
        same(routed.get(id)!.x, acc.x);
        same(routed.get(id)!.y, acc.y);
        // Client-style (vehiclePhysics.ts updateGround): the body's own contact list.
        let x = 0;
        let y = 0;
        let n = 0;
        for (let ce = vehicles[id]!.getContactList(); ce; ce = ce.next) {
          const c = ce.contact;
          if (!c.isTouching() || !c.isEnabled()) continue;
          const carIsA = c.fa.getBody() === vehicles[id];
          const other = tag(carIsA ? c.fb : c.fa);
          if (!other || other.kind !== 'surface') continue;
          if (c.wm.pointCount === 0) continue;
          x += carIsA ? -c.wm.normal.x : c.wm.normal.x;
          y += carIsA ? -c.wm.normal.y : c.wm.normal.y;
          n++;
        }
        let qx = 0;
        let qy = 0;
        let qn = 0;
        touchingContactsOf(vehicles[id]!, isSurface, (c) => {
          qx += c.normal.x;
          qy += c.normal.y;
          qn++;
        });
        assert.equal(qn, n);
        assert.equal(n, acc.n);
        same(qx, x);
        same(qy, y);
        same(qx, acc.x);
        same(qy, acc.y);
      }
    }
  });
});
