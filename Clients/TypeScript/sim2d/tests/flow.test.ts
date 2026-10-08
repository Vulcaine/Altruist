import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { DeterministicRandom } from '../src/math/index.ts';
import {
  AIContext,
  ButtonEdges,
  Countdown,
  EntityRegistry,
  EntitySteps,
  EventSink,
  FirstMatch,
  FirstMatchActions,
  IdMask32,
  InputLatch,
  ModifierStack,
  NO_MATCH,
  nullEventSink,
  OverlapLatch,
  OverlapLatch32,
  StateContext,
  StateMachine,
  StateMachineBuilder,
  Stick,
  TickPipeline,
  TimerSet,
  UtilitySelector,
} from '../src/flow/index.ts';
import { floats, same, vectors } from './helpers.ts';

function lcg(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

// ── FirstMatch ──────────────────────────────────────────────────────────────

interface HitCase {
  zoneMult: number;
  superStrike: boolean;
  kick: boolean;
  perfect: boolean;
  dash: boolean;
}
const SUPER = 3.1, NOSE = 1.35, FLIP_KICK = 1.7, PERFECT = 1.45, DASH = 1.25;

function inlineHitMultiplier(c: HitCase): number {
  let mult = c.zoneMult;
  if (c.superStrike) mult = SUPER;
  else if (c.kick) mult = Math.max(mult, NOSE) * FLIP_KICK;
  else if (c.perfect) mult *= PERFECT;
  else if (c.dash) mult *= DASH;
  return mult;
}

const hitMultiplier = FirstMatch.create<HitCase, number>()
  .whenValue('super', (c) => c.superStrike, SUPER)
  .when('kick', (c) => c.kick, (c) => Math.max(c.zoneMult, NOSE) * FLIP_KICK)
  .when('perfect', (c) => c.perfect, (c) => c.zoneMult * PERFECT)
  .when('dash', (c) => c.dash, (c) => c.zoneMult * DASH)
  .otherwise((c) => c.zoneMult)
  .build();

interface StrikeCase {
  flipPlanned: boolean;
  resetChainSpot: boolean;
  high: boolean;
  jumpNow: boolean;
  behind: boolean;
  near: boolean;
  flipKickSpot: boolean;
  liftSpot: boolean;
  comboRate: number;
  rng: DeterministicRandom;
}

function inlineStrike(s: StrikeCase): string {
  if (!s.flipPlanned && s.resetChainSpot) {
    s.flipPlanned = true;
    if (s.rng.chance(s.comboRate)) return 'resetChain';
  }
  if (s.high) return s.jumpNow ? 'jump' : 'approach';
  if (!s.behind && s.near) return 'getBehind';
  if (s.behind && !s.flipPlanned && s.flipKickSpot) {
    s.flipPlanned = true;
    if (s.rng.chance(s.comboRate)) return 'flipKick';
  }
  if (s.behind && s.liftSpot) return 'lift';
  if (!s.behind) return 'goAround';
  return 'drive';
}

const strike = FirstMatch.create<StrikeCase, string>()
  .try('reset-chain', (s) => {
    if (s.flipPlanned || !s.resetChainSpot) return NO_MATCH;
    s.flipPlanned = true;
    return s.rng.chance(s.comboRate) ? 'resetChain' : NO_MATCH;
  })
  .when('high', (s) => s.high, (s) => (s.jumpNow ? 'jump' : 'approach'))
  .whenValue('get-behind', (s) => !s.behind && s.near, 'getBehind')
  .try('flip-kick', (s) => {
    if (!s.behind || s.flipPlanned || !s.flipKickSpot) return NO_MATCH;
    s.flipPlanned = true;
    return s.rng.chance(s.comboRate) ? 'flipKick' : NO_MATCH;
  })
  .whenValue('lift', (s) => s.behind && s.liftSpot, 'lift')
  .whenValue('go-around', (s) => !s.behind, 'goAround')
  .otherwiseValue('drive')
  .build();

describe('flow: FirstMatch', () => {
  it('reproduces the hit multiplier ladder', () => {
    let i = 0;
    for (const z of floats(3000, 3)) {
      const c = { zoneMult: z, superStrike: (i & 1) !== 0, kick: (i & 2) !== 0, perfect: (i & 4) !== 0, dash: (i & 8) !== 0 };
      same(hitMultiplier.evaluate(c), inlineHitMultiplier(c));
      i++;
    }
  });

  it('try rules reproduce a tactic ladder with latches and rng draws', () => {
    const r = lcg(7);
    const rngA = new DeterministicRandom(42);
    const rngB = new DeterministicRandom(42);
    for (let i = 0; i < 5000; i++) {
      const bits = Math.floor(r() * 0x7fffffff);
      const make = (rng: DeterministicRandom): StrikeCase => ({
        flipPlanned: (bits & 1) !== 0, resetChainSpot: (bits & 2) !== 0, high: (bits & 4) !== 0 && (bits & 64) !== 0,
        jumpNow: (bits & 8) !== 0, behind: (bits & 16) !== 0, near: (bits & 32) !== 0, flipKickSpot: (bits & 128) !== 0,
        liftSpot: (bits & 256) !== 0, comboRate: (bits % 100) / 100, rng,
      });
      const a = make(rngA);
      const b = make(rngB);
      assert.equal(strike.evaluate(b), inlineStrike(a));
      assert.equal(b.flipPlanned, a.flipPlanned);
      assert.equal(rngB.state, rngA.state);
    }
  });

  it('stops at the first match; predicates run once in order', () => {
    const log: string[] = [];
    const fm = FirstMatch.create<number, string>()
      .whenValue('a', (x) => (log.push('a'), x === 1), 'A')
      .when('b', (x) => (log.push('b'), x >= 2), (x) => 'B' + x)
      .whenValue('c', () => (log.push('c'), true), 'C')
      .build();
    assert.equal(fm.evaluate(2), 'B2');
    assert.deepEqual(log, ['a', 'b']);
    assert.equal(fm.evaluate(0), 'C');
    assert.equal(fm.nameOf(fm.lastRule), 'c');
    const none = FirstMatch.create<number, number>().whenValue('p', (x) => x > 0, 1).build();
    assert.equal(none.tryEvaluate(-1), NO_MATCH);
    assert.throws(() => none.evaluate(-1));
  });

  it('actions run only the first matching branch', () => {
    const ran: string[] = [];
    const modes = FirstMatchActions.create<number>()
      .when('jump', (x) => x === 0, () => ran.push('jump'))
      .try('flip', (x) => (x === 1 ? (ran.push('flip'), true) : false))
      .when('drive', (x) => x < 5, () => ran.push('drive'))
      .otherwise(() => ran.push('air'))
      .build();
    for (const x of [0, 1, 2, 9]) modes.run(x);
    assert.deepEqual(ran, ['jump', 'flip', 'drive', 'air']);
    assert.equal(modes.lastRule, -1);
  });

  it('builders insert, replace and remove by name', () => {
    const b = FirstMatch.create<number, string>().whenValue('a', () => false, 'a').whenValue('c', () => false, 'c');
    b.after('a').whenValue('b', () => false, 'b');
    b.before('a').whenValue('first', (x) => x === 1, 'first');
    b.replacing('c').whenValue('c2', (x) => x === 3, 'c2');
    assert.deepEqual([...b.names], ['first', 'a', 'b', 'c2']);
    b.remove('b');
    const fm = b.otherwiseValue('none').build();
    assert.equal(fm.evaluate(3), 'c2');
    assert.throws(() => FirstMatch.create<number, number>().whenValue('a', () => true, 1).whenValue('a', () => true, 2));
    assert.throws(() => FirstMatch.create<number, number>().before('nope'));
    assert.throws(() => FirstMatch.create<number, number>().whenValue('a', () => true, 1).after('a').build());
  });
});

// ── ModifierStack ───────────────────────────────────────────────────────────

interface Grav {
  dashTime: number; stallTime: number; groundNy: number; speed: number; edgeFlight: number;
  bounceUsed: boolean; stallEnded: boolean; grounded: boolean; held: boolean; dashing: boolean; stalling: boolean;
}
const G = { duration: 0.18, dashGravity: 0.15, stallMul: 0.35, stallMax: 0.6, afterglow: 0.12, wall: 1.6, dt: 1 / 60 };
const drag = (v: Grav) => 1 - 0.4 * Math.min(1, Math.max(0, (v.speed - 20) / 8));
const steep = (v: Grav) => (v.groundNy < 0 ? 1 : 1 - v.groundNy);
const onSteep = (v: Grav) => (v.grounded || v.edgeFlight > 0) && v.groundNy < 0.95;

function inlineGravity(v: Grav): number {
  let scale = 1;
  if (v.dashTime < G.duration && !v.bounceUsed) scale = G.dashGravity;
  else if (!v.stallEnded && !v.grounded) {
    if (v.held && v.stallTime < G.stallMax) {
      scale = G.stallMul;
      v.stallTime += G.dt;
    } else v.stallEnded = true;
  }
  if (!v.held) v.stallEnded = true;
  const glow = v.dashTime - G.duration;
  if (!v.grounded && !v.bounceUsed && glow >= 0 && glow < G.afterglow) {
    const u = glow / G.afterglow;
    scale = Math.min(scale, G.dashGravity + (1 - G.dashGravity) * u * u);
  }
  if (onSteep(v)) {
    scale *= 1 + (G.wall - 1) * Math.min(1, steep(v) * 1.5);
    scale *= 1 - (1 - drag(v)) * Math.min(1, steep(v) * 1.5);
  }
  return scale;
}

const gravity = ModifierStack.create<Grav>()
  .set('dash', G.dashGravity, (v) => v.dashing)
  .else().set('stall', G.stallMul, (v) => v.stalling)
  .min('afterglow', (v) => {
    const u = (v.dashTime - G.duration) / G.afterglow;
    return G.dashGravity + (1 - G.dashGravity) * u * u;
  }, (v) => {
    const glow = v.dashTime - G.duration;
    return !v.grounded && !v.bounceUsed && glow >= 0 && glow < G.afterglow;
  })
  .mul('wall', (v) => 1 + (G.wall - 1) * Math.min(1, steep(v) * 1.5), onSteep)
  .mul('grip', (v) => 1 - (1 - drag(v)) * Math.min(1, steep(v) * 1.5), onSteep)
  .build();

function stackGravity(v: Grav): number {
  v.dashing = v.dashTime < G.duration && !v.bounceUsed;
  v.stalling = false;
  if (!v.dashing && !v.stallEnded && !v.grounded) {
    if (v.held && v.stallTime < G.stallMax) {
      v.stalling = true;
      v.stallTime += G.dt;
    } else v.stallEnded = true;
  }
  if (!v.held) v.stallEnded = true;
  return gravity.apply(1, v);
}

describe('flow: ModifierStack', () => {
  it('reproduces the gravity block', () => {
    const r = lcg(11);
    for (let i = 0; i < 20000; i++) {
      const v: Grav = {
        dashTime: r() * 0.5, stallTime: r() * 0.7, groundNy: r() * 2 - 1, speed: r() * 35, edgeFlight: r() < 0.25 ? 0.2 : 0,
        bounceUsed: r() < 0.25, stallEnded: r() < 0.33, grounded: r() < 0.5, held: r() < 0.5, dashing: false, stalling: false,
      };
      const w = { ...v };
      same(stackGravity(w), inlineGravity(v));
      same(w.stallTime, v.stallTime);
      assert.equal(w.stallEnded, v.stallEnded);
    }
  });

  it('else chains take at most one branch; ops apply in order', () => {
    const s = ModifierStack.create<number>()
      .set('a', 1, (x) => x === 1)
      .else().set('b', 2, (x) => x === 2)
      .else().set('c', 3)
      .add('after', 10, (x) => x === 1)
      .else().add('after-else', 20)
      .build();
    assert.equal(s.apply(0, 1), 11);
    assert.equal(s.apply(0, 2), 22);
    assert.equal(s.apply(0, 7), 23);
    const calls: string[] = [];
    const t = ModifierStack.create<number>()
      .add('add', 0.1).mul('mul', 3)
      .add('skipped', () => (calls.push('skipped'), 100), (c) => c > 5)
      .max('max', () => (calls.push('max'), 0.5)).min('min', 0.7).map('map', (c, x) => x - c)
      .build();
    same(t.apply(0.2, 1), Math.min(Math.max((0.2 + 0.1) * 3, 0.5), 0.7) - 1);
    assert.deepEqual(calls, ['max']);
    assert.equal(t.opOf(2), 'add');
  });
});

// ── UtilitySelector (brain.ts decision) ─────────────────────────────────────

type Role = 'kickoff' | 'attack' | 'defend' | 'shadow' | 'retreat' | 'support' | 'refill';
const DT = 1 / 60;
const PASSIVE_LIMIT = 6;

interface Agent {
  forced: boolean; firstMan: boolean; threat: boolean; onKeeper: boolean; wrongSide: boolean; ballOwnHalf: boolean;
  padReady: boolean; superCharge: boolean; camping: boolean; adv: number; challengeMargin: number; threatTime: number;
  noise: number; roleHold: number; elite: number; dashCharges: number; decisionTicks: number;
  role: Role; prevRole: Role; roleSince: number; campTicks: number; switches: number; rng: DeterministicRandom; u: Record<Role, number> | null;
}

function setRole(a: Agent, r: Role, now: number): void {
  if (r === a.role) return;
  if (r !== 'kickoff' && a.role !== 'kickoff') a.switches++;
  a.prevRole = a.role;
  a.role = r;
  a.roleSince = now;
}

/** brain.ts-style decision: Record-keyed scores, noise in object key order. */
function inlineDecide(a: Agent, now: number): void {
  if (a.forced) {
    setRole(a, 'kickoff', now);
    return;
  }
  const noise = () => a.rng.gaussian() * a.noise;
  const u: Record<Role, number> = { kickoff: -9, attack: 0, defend: 0, shadow: 0, retreat: 0, support: 0, refill: 0 };
  u.attack = (a.firstMan ? 1 : 0.15) + Math.max(-1.2, Math.min(1, (a.adv - a.challengeMargin) * 1.5)) - (a.wrongSide ? 1.2 : 0);
  u.defend = a.threat ? 1.7 + Math.max(0, 1.5 - a.threatTime) : 0;
  if (a.threat && a.onKeeper) u.defend += 1;
  u.retreat = a.wrongSide ? 0.9 + (a.ballOwnHalf ? 0.5 : 0) : 0;
  u.shadow = !a.wrongSide && a.firstMan ? 0.75 + (a.ballOwnHalf ? 0.2 : 0) - Math.max(0, a.adv) * 0.5 : 0.2;
  u.support = a.firstMan ? 0 : 1.05;
  u.refill = a.dashCharges === 0 && a.padReady && !a.threat && a.superCharge ? 0.85 : a.dashCharges === 0 && a.padReady && !a.threat ? 0.5 : 0;
  if (!a.threat) u.defend = -Infinity;
  if (!a.wrongSide) u.retreat = -Infinity;
  if (a.firstMan) u.support = -Infinity;
  if (u.refill === 0) u.refill = -Infinity;
  for (const r of Object.keys(u) as Role[]) if (r !== 'kickoff') u[r] += noise();
  const held = (now - a.roleSince) * DT < a.roleHold;
  if (a.role !== 'kickoff') u[a.role] += held ? 0.6 : 0.3;
  let best: Role = a.role === 'kickoff' ? 'attack' : a.role;
  for (const r of Object.keys(u) as Role[]) if (u[r] > u[best]) best = r;
  if (a.threat && a.threatTime < 1.2 && best !== 'defend' && u.defend > 1.5) best = 'defend';
  a.campTicks = a.camping ? a.campTicks + a.decisionTicks : 0;
  if (a.camping && a.campTicks * DT > PASSIVE_LIMIT * (1 - 0.6 * a.elite) && (best === 'shadow' || best === 'defend' || best === 'retreat' || best === 'refill')) {
    best = a.firstMan ? 'attack' : 'support';
  }
  a.u = u;
  if (best !== a.role) setRole(a, best, now);
}

function roleSelector() {
  return UtilitySelector.create<Role, Agent, DeterministicRandom>()
    .force('kickoff', (a) => a.forced, 'kickoff')
    .option('kickoff', -9, { noisy: false, transient: true })
    .option('attack', (a) => (a.firstMan ? 1 : 0.15) + Math.max(-1.2, Math.min(1, (a.adv - a.challengeMargin) * 1.5)) - (a.wrongSide ? 1.2 : 0))
    .option('defend', (a) => {
      let s = a.threat ? 1.7 + Math.max(0, 1.5 - a.threatTime) : 0;
      if (a.threat && a.onKeeper) s += 1;
      return s;
    }, { veto: (a) => !a.threat })
    .option('shadow', (a) => (!a.wrongSide && a.firstMan ? 0.75 + (a.ballOwnHalf ? 0.2 : 0) - Math.max(0, a.adv) * 0.5 : 0.2))
    .option('retreat', (a) => (a.wrongSide ? 0.9 + (a.ballOwnHalf ? 0.5 : 0) : 0), { veto: (a) => !a.wrongSide })
    .option('support', (a) => (a.firstMan ? 0 : 1.05), { veto: (a) => a.firstMan })
    .option('refill', (a) => (a.dashCharges === 0 && a.padReady && !a.threat && a.superCharge ? 0.85 : a.dashCharges === 0 && a.padReady && !a.threat ? 0.5 : 0), {
      veto: (_a, s) => s === 0,
    })
    .noise((a) => a.noise)
    .inertia((a) => a.roleHold, 0.6, 0.3, DT)
    .fallbackTo('attack')
    .startWith('kickoff')
    .override('threat', (a, best, u) => (a.threat && a.threatTime < 1.2 && best !== 'defend' && u.of('defend') > 1.5 ? 'defend' : best))
    .override('passive-guard', (a, best) => {
      a.campTicks = a.camping ? a.campTicks + a.decisionTicks : 0;
      return a.camping && a.campTicks * DT > PASSIVE_LIMIT * (1 - 0.6 * a.elite) && (best === 'shadow' || best === 'defend' || best === 'retreat' || best === 'refill')
        ? a.firstMan ? 'attack' : 'support'
        : best;
    })
    .build();
}

function newAgent(seed: number): Agent {
  return {
    forced: false, firstMan: false, threat: false, onKeeper: false, wrongSide: false, ballOwnHalf: false, padReady: false,
    superCharge: false, camping: false, adv: 0, challengeMargin: 0.2, threatTime: 0, noise: 0, roleHold: 0.8, elite: 0,
    dashCharges: 0, decisionTicks: 6, role: 'kickoff', prevRole: 'kickoff', roleSince: 0, campTicks: 0, switches: 0,
    rng: new DeterministicRandom(seed), u: null,
  };
}

describe('flow: UtilitySelector', () => {
  it('reproduces the role decision with the same rng draws', () => {
    const sel = roleSelector();
    const inline = newAgent(99);
    const mine = newAgent(99);
    const r = lcg(3);
    let now = 0;
    for (let i = 0; i < 6000; i++) {
      const bits = Math.floor(r() * 0x7fffffff);
      const fill = (a: Agent) => {
        a.forced = i < 3 || bits % 97 === 0;
        a.firstMan = (bits & 1) !== 0; a.threat = (bits & 2) !== 0; a.onKeeper = (bits & 4) !== 0; a.wrongSide = (bits & 8) !== 0;
        a.ballOwnHalf = (bits & 16) !== 0; a.padReady = (bits & 32) !== 0; a.superCharge = (bits & 64) !== 0; a.camping = (bits & 0x380) !== 0;
        a.adv = (((bits >> 10) % 400) - 200) / 100; a.threatTime = ((bits >> 12) % 300) / 100; a.noise = ((bits >> 3) % 5) * 0.15;
        a.elite = (bits % 3) * 0.5; a.dashCharges = (bits >> 20) % 3;
      };
      fill(inline);
      fill(mine);
      inlineDecide(inline, now);
      const best = sel.decide(mine, mine.rng, now);
      if (best !== mine.role) {
        setRole(mine, best, now);
        sel.choose(best, now);
      }
      assert.equal(mine.role, inline.role, `decision ${i}`);
      assert.equal(mine.roleSince, inline.roleSince);
      assert.equal(mine.campTicks, inline.campTicks);
      assert.equal(mine.switches, inline.switches);
      assert.equal(mine.rng.state, inline.rng.state);
      assert.equal(sel.current, inline.role);
      if (!inline.forced) for (const role of sel.optionList) same(sel.scores.of(role), inline.u![role]);
      else assert.equal(sel.lastForced, 'kickoff');
      now += 6;
    }
    assert.ok(inline.switches > 100);
  });

  it('draws noise for vetoed options too and never picks them', () => {
    const sel = UtilitySelector.create<string, number, DeterministicRandom>()
      .option('a', 0).option('b', 5, { veto: () => true }).option('c', 0.1).noise(() => 10).build();
    const rng = new DeterministicRandom(1);
    const ref = new DeterministicRandom(1);
    for (let i = 0; i < 200; i++) {
      assert.notEqual(sel.decide(0, rng, i), 'b');
      assert.equal(sel.scores.of('b'), -Infinity);
      ref.gaussian(); ref.gaussian(); ref.gaussian();
      assert.equal(rng.state, ref.state);
    }
  });
});

// ── TickPipeline ────────────────────────────────────────────────────────────

interface Unit { id: number; demolished: number }
interface World { units: Unit[]; log: string[]; frozen: boolean }

function inlineStep(w: World, dt: number): void {
  w.log.push('clock');
  for (const u of w.units) {
    if (u.demolished > 0) {
      u.demolished = Math.max(0, u.demolished - dt);
      if (u.demolished === 0) w.log.push(`respawn${u.id}`);
      continue;
    }
    w.log.push(`input${u.id}`);
    if (w.frozen) continue;
    w.log.push(`move${u.id}`);
  }
  if (w.frozen) {
    w.log.push('frozen-phase');
    return;
  }
  w.log.push('physics');
}

describe('flow: TickPipeline', () => {
  it('runs steps, entities, skips and stops in the inline order', () => {
    const dt = 1 / 60;
    const pipeline = TickPipeline.create<World>()
      .step('clock', (w) => w.log.push('clock'))
      .forEach('units', (w) => w.units, (s: EntitySteps<World, Unit>) => s
        .skipWhen('demolished', (w, u) => {
          if (!Countdown.isRunning(u.demolished)) return false;
          if (Countdown.tickField(u, 'demolished', dt)) w.log.push(`respawn${u.id}`);
          return true;
        })
        .step('input', (w, u) => w.log.push(`input${u.id}`))
        .skipWhen('frozen', (w) => w.frozen)
        .step('move', (w, u) => w.log.push(`move${u.id}`)))
      .stopWhen('frozen', (w) => (w.frozen ? (w.log.push('frozen-phase'), true) : false))
      .step('physics', (w) => w.log.push('physics'))
      .build();
    const mk = (): World => ({ units: [0, 1, 2, 3].map((id) => ({ id, demolished: id === 2 ? 0.05 : 0 })), log: [], frozen: false });
    const a = mk();
    const b = mk();
    for (let t = 0; t < 12; t++) {
      a.frozen = b.frozen = t % 5 === 0;
      inlineStep(a, dt);
      assert.equal(pipeline.run(b), !b.frozen);
      assert.equal(pipeline.stoppedAt, b.frozen ? pipeline.indexOf('frozen') : -1);
    }
    assert.deepEqual(b.log, a.log);
    assert.ok(b.log.includes('respawn2'));
  });

  it('steps can be inserted, replaced and removed by name', () => {
    const log: string[] = [];
    const entity = new EntitySteps<number, number>().step('a', (_, e) => log.push(`a${e}`)).step('c', (_, e) => log.push(`c${e}`));
    entity.after('a').step('b', (_, e) => log.push(`b${e}`));
    const b = TickPipeline.create<number>()
      .step('one', () => log.push('one'))
      .forEach('each', () => [1, 2], entity)
      .step('three', () => log.push('three'));
    b.before('three').step('two', () => log.push('two'));
    b.replacing('one').step('first', () => log.push('first'));
    b.remove('three');
    entity.remove('c');
    b.build().run(0);
    assert.deepEqual(log, ['first', 'a1', 'b1', 'a2', 'b2', 'two']);
  });
});

// ── OverlapLatch / IdMask32 ─────────────────────────────────────────────────

interface Car { id: number; team: number; demolished: boolean; ramMask: number; ram: OverlapLatch32 }

describe('flow: OverlapLatch', () => {
  it('OverlapLatch32 reproduces the ram mask semantics', () => {
    const r = lcg(5);
    const cars = (): Car[] => [0, 1, 2, 3, 4, 5].map((i) => ({ id: i * 7, team: i % 2, demolished: false, ramMask: 0, ram: new OverlapLatch32() }));
    const inline = cars();
    const latch = cars();
    let hits = 0;
    for (let step = 0; step < 3000; step++) {
      const seed = Math.floor(r() * 0x7fffffff);
      const overlaps = (a: Car, b: Car) => ((seed >> ((a.id + b.id) % 23)) & 3) !== 0;
      const qualifies = (a: Car, b: Car) => ((seed >> ((a.id * 3 + b.id) % 19)) & 1) !== 0;
      for (let i = 0; i < 6; i++) inline[i]!.demolished = latch[i]!.demolished = ((seed >> i) & 15) === 0;
      for (let i = 0; i < 6; i++) {
        if (inline[i]!.demolished) continue;
        const a = inline[i]!;
        const prevMask = a.ramMask;
        let mask = 0;
        let h1: Car | null = null;
        for (const b of inline) {
          if (b === a || b.team === a.team || b.demolished) continue;
          if (!overlaps(a, b)) continue;
          const bit = 1 << (b.id & 31);
          mask |= bit;
          if (h1 || prevMask & bit) continue;
          if (qualifies(a, b)) h1 = b;
        }
        a.ramMask = mask;

        const c = latch[i]!;
        c.ram.begin();
        let h2: Car | null = null;
        for (const b of latch) {
          if (b === c || b.team === c.team || b.demolished) continue;
          if (!overlaps(c, b)) continue;
          if (!c.ram.record(b.id) || h2) continue;
          if (qualifies(c, b)) h2 = b;
        }
        assert.equal(h2?.id ?? -1, h1?.id ?? -1);
        if (h1) hits++;
        assert.equal(c.ram.current, a.ramMask);
      }
    }
    assert.ok(hits > 100);
  });

  it('IdMask32 collects a captured set; OverlapLatch reports fresh entries', () => {
    const cars = Array.from({ length: 40 }, (_, i) => ({ id: i, team: i % 2, demolished: i % 9 === 0 }));
    const a = cars[3]!;
    let inline = 0;
    for (const b of cars) {
      if (b === a || b.team === a.team || b.demolished) continue;
      if (b.id % 3 !== 0) inline |= 1 << (b.id & 31);
    }
    assert.equal(IdMask32.collect(cars, (b) => b !== a && b.team !== a.team && !b.demolished && b.id % 3 !== 0, (b) => b.id), inline);
    assert.equal(IdMask32.withId(0, 31), -2147483648);
    const latch = new OverlapLatch<string>();
    latch.begin();
    assert.equal(latch.record('a'), true);
    assert.equal(latch.record('a'), false);
    latch.begin();
    assert.equal(latch.record('a'), false);
    assert.equal(latch.record('b'), true);
    assert.deepEqual([...latch.entered], ['b']);
  });
});

// ── Countdown / TimerSet / input ────────────────────────────────────────────

describe('flow: Countdown, TimerSet, input', () => {
  it('Countdown fields reproduce the timer block with expiry edges', () => {
    type T = { dashTime: number; cooldown: number; flipTime: number; demolished: number; flipEnds: number; respawns: number };
    const mk = (): T => ({ dashTime: 0, cooldown: 0, flipTime: 0, demolished: 0, flipEnds: 0, respawns: 0 });
    const a = mk();
    const b = mk();
    const r = lcg(9);
    const dt = 1 / 60;
    for (let i = 0; i < 20000; i++) {
      if (r() < 0.1) {
        const v = r() * 0.4;
        const k = Math.floor(r() * 4);
        if (k === 0) a.cooldown = b.cooldown = v;
        else if (k === 1) a.flipTime = b.flipTime = v;
        else if (k === 2) a.demolished = b.demolished = v;
        else a.dashTime = b.dashTime = 0;
      }
      a.dashTime = Math.min(a.dashTime + dt, 1e6);
      a.cooldown = Math.max(0, a.cooldown - dt);
      if (a.flipTime > 0) {
        a.flipTime = Math.max(0, a.flipTime - dt);
        if (a.flipTime === 0) a.flipEnds++;
      }
      if (a.demolished > 0) {
        a.demolished = Math.max(0, a.demolished - dt);
        if (a.demolished === 0) a.respawns++;
      }
      Countdown.countUpField(b, 'dashTime', dt, 1e6);
      b.cooldown = Countdown.tick(b.cooldown, dt);
      if (Countdown.tickRunningField(b, 'flipTime', dt)) b.flipEnds++;
      if (Countdown.isRunning(b.demolished) && Countdown.tickField(b, 'demolished', dt)) b.respawns++;
      same(b.dashTime, a.dashTime);
      same(b.cooldown, a.cooldown);
      same(b.flipTime, a.flipTime);
      same(b.demolished, a.demolished);
    }
    assert.equal(b.flipEnds, a.flipEnds);
    assert.equal(b.respawns, a.respawns);
    assert.ok(a.respawns > 10);
  });

  it('TimerSet matches the cooldown array loop', () => {
    const inline = [0, 0, 0, 0, 0];
    const set = new TimerSet(['a', 'b', 'c', 'd', 'e']);
    const r = lcg(2);
    for (let i = 0; i < 5000; i++) {
      if (r() < 0.125) {
        const k = Math.floor(r() * 5);
        const v = r();
        inline[k] = v;
        set.start(k, v);
      }
      const before = [...inline];
      for (let k = 0; k < inline.length; k++) inline[k] = Math.max(0, inline[k]! - 1 / 60);
      set.tick(1 / 60);
      for (let k = 0; k < 5; k++) {
        same(set.remaining[k]!, inline[k]!);
        assert.equal(set.expiredAt(k), before[k]! > 0 && inline[k] === 0);
      }
    }
    assert.equal(set.indexOf('c'), 2);
  });

  it('InputLatch and ButtonEdges reproduce repeat-last-input and pressed edges', () => {
    type Frame = { moveX: number; moveY: number; buttons: number };
    const r = lcg(4);
    let lastX = 0, lastY = 0, lastButtons = 0, prevButtons = 0;
    const latch = new InputLatch<Frame>({ moveX: 0, moveY: 0, buttons: 0 });
    const edges = new ButtonEdges();
    for (let i = 0; i < 5000; i++) {
      const inputs = new Map<number, Frame>();
      if (r() < 0.66) inputs.set(7, { moveX: r() * 2 - 1, moveY: r() * 2 - 1, buttons: Math.floor(r() * 64) });
      const input = inputs.get(7) ?? { moveX: lastX, moveY: lastY, buttons: lastButtons };
      const pressed = input.buttons & ~prevButtons;
      prevButtons = input.buttons;
      lastX = input.moveX;
      lastY = input.moveY;
      lastButtons = input.buttons;
      const resolved = latch.resolveFrom(inputs, 7);
      assert.deepEqual(resolved, input);
      assert.equal(edges.update(resolved.buttons), pressed);
      assert.equal(edges.held, prevButtons);
    }
    for (const v of vectors(500, 1.5)) {
      assert.equal(Stick.axisBeyond(v.x, 0.3), Math.abs(v.x) > 0.3);
      assert.equal(Stick.insideDeadzone(v.x, v.y, 0.35), Math.sqrt(v.x * v.x + v.y * v.y) < 0.35);
      assert.equal(Stick.beyond(v.x, v.y, 0.35), !Stick.insideDeadzone(v.x, v.y, 0.35));
    }
  });
});

// ── EntityRegistry / EventSink ──────────────────────────────────────────────

describe('flow: EntityRegistry, EventSink', () => {
  it('EntityRegistry keeps the sorted-list order with fast lookup', () => {
    let reference: { id: number }[] = [];
    const reg = new EntityRegistry<{ id: number }>();
    const r = lcg(8);
    for (let i = 0; i < 3000; i++) {
      const id = Math.floor(r() * 60);
      if (r() < 0.33) {
        reference = reference.filter((e) => e.id !== id);
        reg.remove(id);
      } else if (!reference.some((e) => e.id === id)) {
        const item = { id };
        reference.push(item);
        reference.sort((a, b) => a.id - b.id);
        reg.add(id, item);
      }
      assert.deepEqual([...reg.items], reference);
      const probe = Math.floor(r() * 60);
      assert.equal(reg.indexOf(probe), reference.findIndex((e) => e.id === probe));
      assert.equal(reg.get(probe), reference.find((e) => e.id === probe));
    }
    assert.ok(reg.count > 0);
    assert.throws(() => reg.add(reg.ids[0]!, { id: -1 }));
  });

  it('EventSink keeps emission order', () => {
    const sink = new EventSink<number>();
    for (let i = 0; i < 5; i++) sink.emit(i);
    sink.push(5);
    assert.deepEqual([...sink], [0, 1, 2, 3, 4, 5]);
    assert.deepEqual(sink.take(), [0, 1, 2, 3, 4, 5]);
    assert.equal(sink.count, 0);
    nullEventSink.emit(1);
  });
});

// ── StateMachine ────────────────────────────────────────────────────────────

class Match extends AIContext {
  timeLeft = 10;
  goalNow = false;
  everyoneSkipped = false;
  events: string[] = [];
}
const PH = { countdown: 3, goalPause: 1.5, replay: 2, dt: 1 / 60 };

function phaseMachine(): StateMachine<Match> {
  const afterGoal = (m: Match) => {
    if (m.timeLeft <= 0) {
      m.events.push('end');
      return 'Ended';
    }
    return 'Countdown';
  };
  const b = new StateMachineBuilder<Match>();
  b.configureState('Countdown').asInitial().handler((m) => {
    if (m.timeInState < PH.countdown) return null;
    m.events.push('kickoff');
    return 'Playing';
  });
  b.configureState('Playing').handler((m, dt) => {
    if (m.goalNow) {
      m.events.push('goal');
      return 'Goal';
    }
    Countdown.tickField(m, 'timeLeft', dt);
    if (m.timeLeft > 0) return null;
    m.events.push('end');
    return 'Ended';
  });
  b.configureState('Goal').handler((m) => (m.timeInState < PH.goalPause ? null : !m.everyoneSkipped ? 'Replay' : afterGoal(m)));
  b.configureState('Replay').handler((m) => (m.timeInState >= PH.replay || m.everyoneSkipped ? afterGoal(m) : null));
  b.configureState('Ended').handler(() => null);
  return new StateMachine(b.build());
}

describe('flow: StateMachine', () => {
  it('hosts match phases with the inline clock and transitions', () => {
    let phase = 'Countdown';
    let phaseTime = 0;
    let timeLeft = 10;
    const events: string[] = [];
    const setPhase = (p: string) => {
      phase = p;
      phaseTime = 0;
    };
    const afterGoal = () => {
      if (timeLeft <= 0) {
        setPhase('Ended');
        events.push('end');
      } else setPhase('Countdown');
    };
    const m = new Match();
    const fsm = phaseMachine();
    fsm.initialize(m);
    const r = lcg(12);
    for (let t = 0; t < 4000; t++) {
      const goal = r() < 1 / 90;
      const skipped = r() < 1 / 150;
      phaseTime += PH.dt;
      const midA = phaseTime;
      if (phase === 'Countdown') {
        if (phaseTime >= PH.countdown) {
          setPhase('Playing');
          events.push('kickoff');
        }
      } else if (phase === 'Playing') {
        if (goal) {
          setPhase('Goal');
          events.push('goal');
        } else {
          timeLeft = Math.max(0, timeLeft - PH.dt);
          if (timeLeft <= 0) {
            setPhase('Ended');
            events.push('end');
          }
        }
      } else if (phase === 'Goal' && phaseTime >= PH.goalPause) {
        if (!skipped) setPhase('Replay');
        else afterGoal();
      } else if (phase === 'Replay' && (phaseTime >= PH.replay || skipped)) afterGoal();

      m.goalNow = goal;
      m.everyoneSkipped = skipped;
      fsm.advance(m, PH.dt);
      same(fsm.timeInState, midA);
      fsm.runState(m, PH.dt);
      assert.equal(fsm.currentStateName, phase, `tick ${t}`);
      same(fsm.timeInState, phaseTime);
      same(m.timeLeft, timeLeft);
    }
    assert.deepEqual(m.events, events);
    assert.ok(events.includes('goal') && events.includes('end'));
  });

  it('re-enters on transitionTo the current state; update = advance + runState; windows', () => {
    let enters = 0;
    let exits = 0;
    const b = new StateMachineBuilder<Match>();
    b.configureState('A').asInitial().duration(1).window('hit', 0.25, 0.5).handler((m) => (m.timeInState >= 0.75 ? 'B' : 'A'))
      .onEnter(() => enters++).onExit(() => exits++);
    b.configureState('B').delay(0.1).handler(() => null);
    const def = b.build();
    const one = new StateMachine(def);
    const two = new StateMachine(def);
    const m1 = new Match();
    const m2 = new Match();
    one.initialize(m1);
    two.initialize(m2);
    let entered = 0;
    let exited = 0;
    for (let i = 0; i < 60; i++) {
      const t1 = one.update(m1, 1 / 30);
      two.advance(m2, 1 / 30);
      assert.equal(two.runState(m2, 1 / 30), t1);
      assert.equal(two.currentStateName, one.currentStateName);
      same(two.timeInState, one.timeInState);
      if (StateContext.windowEntered(m1, 'hit')) entered++;
      if (StateContext.windowExited(m1, 'hit')) exited++;
    }
    assert.equal(entered, 1);
    assert.equal(exited, 1);
    one.transitionTo(m1, 'B');
    one.transitionTo(m1, 'A');
    one.transitionTo(m1, 'A');
    assert.equal(one.timeInState, 0);
    assert.equal(enters, 2 + 2);
    assert.equal(exits, 3);
  });
});
