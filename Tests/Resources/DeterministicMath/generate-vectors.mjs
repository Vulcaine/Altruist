// Generates the DeterministicMath test vectors from V8's Math.* (fdlibm as V8 ships it).
//
//   node Tests/Resources/DeterministicMath/generate-vectors.mjs
//
// Writes one gzipped binary file per function next to this script (<name>.bin.gz). Each record is
// a run of little-endian IEEE 754 doubles stored as raw 64-bit patterns: [x, result] for the unary
// functions, [a, b, result] for atan2 (a = y, b = x) and pow (a = base, b = exponent). Inputs come
// from a fixed-seed generator, so re-running on the same Node version reproduces the files exactly;
// a diff after a Node upgrade means V8's Math.* changed. Tests/Altruist/Numerics/DeterministicMathTests.cs
// checks Altruist.Numerics.DeterministicMath against every record bit for bit.

import { gzipSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT_DIR = dirname(fileURLToPath(import.meta.url));
const UNARY_COUNT = 20000;
const BINARY_COUNT = 20000;

// ── Bits ──────────────────────────────────────────────────────────────────

const scratch = new DataView(new ArrayBuffer(8));

function fromWords(hi, lo) {
  scratch.setUint32(0, hi >>> 0, true);
  scratch.setUint32(4, lo >>> 0, true);
  return scratch.getFloat64(0, true);
}

function fromBits(bigint) {
  scratch.setBigUint64(0, BigInt.asUintN(64, bigint), true);
  return scratch.getFloat64(0, true);
}

function toBits(x) {
  scratch.setFloat64(0, x, true);
  return scratch.getBigUint64(0, true);
}

/** x moved by `steps` units in the last place (through zero, across exponents). */
function ulpStep(x, steps) {
  if (!Number.isFinite(x)) return x;
  // Map doubles onto a monotonic integer line: negatives are mirrored below zero.
  const b = toBits(x);
  const ordered = b >> 63n ? -(b & 0x7fffffffffffffffn) : b;
  const moved = ordered + BigInt(steps);
  return moved < 0n ? fromBits((-moved) | 0x8000000000000000n) : fromBits(moved);
}

function withNeighbors(values, radius) {
  const out = [];
  for (const v of values) for (let s = -radius; s <= radius; s++) out.push(ulpStep(v, s));
  return out;
}

// ── Seeded generator (xorshift128) ────────────────────────────────────────

function createRandom(seed) {
  let a = seed >>> 0 || 1, b = 0x9e3779b9, c = 0x243f6a88, d = 0xb7e15162;
  const next = () => {
    const t = a ^ (a << 11);
    a = b; b = c; c = d;
    d = (d ^ (d >>> 19) ^ (t ^ (t >>> 8))) >>> 0;
    return d;
  };
  for (let i = 0; i < 32; i++) next();
  const unit = () => (next() * 4294967296 + next()) / 18446744073709551616;
  return {
    u32: next,
    unit,
    uniform: (lo, hi) => lo + (hi - lo) * unit(),
    int: (lo, hi) => lo + Math.floor(unit() * (hi - lo + 1)),
    sign: () => (next() & 1 ? -1 : 1),
    anyBits: () => fromWords(next(), next()),
    /** |x| = 2^e * 1.m with e uniform in [minExp, maxExp]; subnormal patterns for e < -1022. */
    logUniform: (minExp, maxExp) => {
      const e = minExp + Math.floor(unit() * (maxExp - minExp + 1));
      // (1.m * 2^(e+1074)) * 2^-1074 rounds once, to a subnormal of magnitude ~2^e.
      if (e < -1022) return fromWords(((e + 1074 + 1023) << 20) | (next() & 0x000fffff), next()) * MIN_SUB;
      return fromWords(((e + 1023) << 20) | (next() & 0x000fffff), next());
    },
  };
}

// ── Inputs ────────────────────────────────────────────────────────────────

const MIN_SUB = 5e-324;
const MAX_SUB = fromWords(0x000fffff, 0xffffffff);
const MIN_NORMAL = 2.2250738585072014e-308;

const COMMON = withNeighbors(
  [
    0, 1, 0.5, 2, 3, 10, 100, 1e5, 1e10, 1e22, 1e100, 1e300, Math.PI, Math.PI / 2, Math.PI / 4, Math.E,
    Math.LN2, Math.SQRT2, Math.SQRT1_2, 2 ** -27, 2 ** -28, 2 ** -54, 2 ** 19 * (Math.PI / 2), 2 ** 20,
    2 ** 31, 2 ** 52, 2 ** 53, 2 ** 64, 2 ** 66, 2 ** 1023, MIN_NORMAL, MAX_SUB, Number.MAX_VALUE,
  ].flatMap((v) => [v, -v]),
  2,
).concat([0, -0, Infinity, -Infinity, NaN, MIN_SUB, -MIN_SUB, fromWords(0x7ff80000, 1), fromWords(0xfff00000, 1)]);

function fill(base, count, random, makers) {
  const out = base.slice(0, count);
  let i = 0;
  while (out.length < count) out.push(makers[i++ % makers.length](random));
  return out;
}

const fullRange = (r) => r.sign() * r.logUniform(-1074, 1023);

function trigInputs(seed) {
  const r = createRandom(seed);
  const halfPiMultiples = [];
  for (let k = 1; k <= 400; k++) halfPiMultiples.push(k * (Math.PI / 2), -k * (Math.PI / 2));
  // Known hard cases for argument reduction (closest doubles to multiples of pi/2).
  const hard = [
    fromWords(0x5fcd6fb1, 0x8b9e86ff), 6381956970095103 * 2 ** 797, 5.319372648326541e255,
    1.0e22, 2 ** 1000, 1.5707963267948966e300, 3.14159265358979e15, 1.0e15 * Math.PI,
  ];
  const base = [
    ...COMMON,
    ...withNeighbors(halfPiMultiples, 2),
    ...withNeighbors(hard.flatMap((v) => [v, -v]), 1),
    ...withNeighbors([0.6744, 0.67434, fromWords(0x3fe59428, 0), fromWords(0x3fd33333, 0), 0.78125, fromWords(0x3fe921fb, 0), fromWords(0x4002d97c, 0), fromWords(0x413921fb, 0x54442d18)].flatMap((v) => [v, -v]), 2),
  ];
  return fill(base, UNARY_COUNT, r, [
    (q) => q.anyBits(),
    fullRange,
    fullRange,
    (q) => q.sign() * q.logUniform(-30, 30),
    (q) => q.sign() * q.logUniform(15, 1023),
    (q) => q.uniform(-Math.PI / 4, Math.PI / 4),
    (q) => q.uniform(-10, 10),
    (q) => q.uniform(-1000, 1000),
    (q) => q.uniform(-1e6, 1e6),
    (q) => q.int(-200000, 200000) * (Math.PI / 2) + q.uniform(-1e-9, 1e-9),
    (q) => ulpStep(q.int(-1000000, 1000000) * (Math.PI / 2), q.int(-3, 3)),
  ]);
}

function atanInputs(seed) {
  const r = createRandom(seed);
  const bounds = [7 / 16, 11 / 16, 19 / 16, 39 / 16, 2 ** 66, 2 ** -27, 1, 1.5, 0.5, fromWords(0x3fdc0000, 0), fromWords(0x3ff30000, 0), fromWords(0x40038000, 0), fromWords(0x44100000, 0)];
  const base = [...COMMON, ...withNeighbors(bounds.flatMap((v) => [v, -v]), 3)];
  return fill(base, UNARY_COUNT, r, [
    (q) => q.anyBits(),
    fullRange,
    (q) => q.sign() * q.logUniform(-40, 70),
    (q) => q.uniform(-3, 3),
    (q) => q.uniform(-50, 50),
    (q) => q.sign() * q.uniform(0.4, 2.5),
  ]);
}

function expInputs(seed) {
  const r = createRandom(seed);
  const oThreshold = 7.09782712893383973096e2;
  const uThreshold = -7.45133219101941108420e2;
  const bounds = [
    oThreshold, uThreshold, -708.3964185322641, -708.4, -709.0895657128241, Math.LN2 / 2, 1.5 * Math.LN2,
    fromWords(0x3fd62e42, 0), fromWords(0x3ff0a2b2, 0), fromWords(0x40862e42, 0), fromWords(0x3e300000, 0), 1, 709, 710, 745, 1024 * Math.LN2,
  ];
  const base = [...COMMON, ...withNeighbors(bounds.flatMap((v) => [v, -v]), 3)];
  return fill(base, UNARY_COUNT, r, [
    (q) => q.anyBits(),
    fullRange,
    (q) => q.uniform(-750, 750),
    (q) => q.uniform(-20, 20),
    (q) => q.uniform(-1.5, 1.5),
    (q) => q.sign() * q.logUniform(-60, 0),
    (q) => q.uniform(705, 711),
    (q) => q.uniform(-746, -700),
    (q) => q.int(-1100, 1100) * Math.LN2 + q.uniform(-1e-6, 1e-6),
  ]);
}

function logInputs(seed) {
  const r = createRandom(seed);
  const bounds = [1, Math.SQRT2, Math.SQRT1_2, 1 + 2 ** -20, 1 - 2 ** -20, fromWords(0x3ff6147a, 0), fromWords(0x3ff6b851, 0), fromWords(0x3fe6a09e, 0x667f3bcd), MIN_NORMAL, MAX_SUB, MIN_SUB, Math.E];
  const base = [...COMMON, ...withNeighbors(bounds, 4)];
  return fill(base, UNARY_COUNT, r, [
    (q) => q.anyBits(),
    (q) => q.logUniform(-1074, 1023),
    (q) => q.logUniform(-1074, 1023),
    (q) => q.logUniform(-1074, -1023),
    (q) => q.uniform(0, 4),
    (q) => q.uniform(0, 1000),
    (q) => 1 + q.sign() * q.logUniform(-52, -1),
    (q) => 1 + q.sign() * q.unit() * 2 ** -20,
    (q) => -q.logUniform(-1074, 1023),
  ]);
}

// ── Pairs ─────────────────────────────────────────────────────────────────

function cross(as, bs) {
  return as.flatMap((a) => bs.map((b) => [a, b]));
}

function atan2Pairs(seed) {
  const r = createRandom(seed);
  const specials = [0, 1, 0.5, 2, 3, Math.PI, 1e-300, 1e300, MIN_SUB, MIN_NORMAL, Number.MAX_VALUE, 2 ** 60, 2 ** -60, Infinity]
    .flatMap((v) => [v, -v]).concat([NaN]);
  const base = [...cross(specials, specials)];
  const pair = (fy, fx) => (q) => [fy(q), fx(q)];
  return fill(base, BINARY_COUNT, r, [
    (q) => [q.anyBits(), q.anyBits()],
    pair(fullRange, fullRange),
    pair((q) => q.uniform(-10, 10), (q) => q.uniform(-10, 10)),
    (q) => { const t = q.uniform(-Math.PI, Math.PI), s = q.logUniform(-20, 20); return [Math.sin(t) * s, Math.cos(t) * s]; },
    pair((q) => q.uniform(-10, 10), () => 1),
    pair((q) => q.sign() * q.logUniform(-10, 10), (q) => q.sign() * q.logUniform(-80, 80)),
    pair((q) => q.sign() * q.logUniform(-80, 80), (q) => q.sign() * q.logUniform(-10, 10)),
    (q) => { const x = q.sign() * q.logUniform(-5, 5); return [ulpStep(x * q.sign(), q.int(-4, 4)), x]; },
  ]);
}

function powPairs(seed) {
  const r = createRandom(seed);
  const xs = [0, 1, 0.5, 2, 3, 10, Math.E, 1e-300, 1e300, MIN_SUB, MAX_SUB, Number.MAX_VALUE, ulpStep(1, 1), ulpStep(1, -1), ulpStep(1, 1000), Infinity]
    .flatMap((v) => [v, -v]).concat([NaN]);
  const ys = [0, 1, 2, 0.5, 3, 1.5, 0.25, 1 / 3, 1023, 1024, 1075, 2 ** 31, 2 ** 31 + 1, 2 ** 31 - 1, 2 ** 52 + 1, 2 ** 53 - 1, 2 ** 53, 2 ** 64, 2 ** 65, 1e300, 1e-300, MIN_SUB, Infinity]
    .flatMap((v) => [v, -v]).concat([NaN]);
  const base = cross(xs, ys);
  const boundary = (targets) => (q) => {
    const x = q.logUniform(-1074, 1023) || 2;
    const t = targets[q.int(0, targets.length - 1)] + q.uniform(-2, 2);
    return [x === 1 ? 2 : x, t / Math.log2(x === 1 ? 2 : x)];
  };
  return fill(base, BINARY_COUNT, r, [
    (q) => [q.anyBits(), q.anyBits()],
    (q) => [q.logUniform(-1074, 1023), q.uniform(-10, 10)],
    (q) => [q.uniform(0, 10), q.uniform(-50, 50)],
    (q) => [q.uniform(0, 2), q.uniform(-1000, 1000)],
    (q) => [-q.logUniform(-60, 60), q.int(-100, 100)],
    (q) => [-q.uniform(0, 3), q.sign() * q.int(0, 2 ** 31) * 2 ** q.int(0, 30)],
    (q) => [-q.uniform(0, 10), q.uniform(-10, 10)],
    (q) => [1 + q.sign() * q.unit() * 2 ** -q.int(20, 52), q.sign() * q.logUniform(31, 70)],
    (q) => [1 + q.sign() * q.unit() * 2 ** -q.int(20, 52), q.sign() * q.logUniform(10, 40)],
    boundary([1024, 1023, -1022, -1074, -1075, -1060]),
    boundary([1024, -1075]),
    (q) => [q.int(2, 1000), q.int(0, 60)],
    (q) => [q.sign() * q.int(2, 50), q.int(-40, 40)],
    (q) => [q.logUniform(-1074, -1023), q.uniform(-2, 2)],
    (q) => [q.sign() * q.logUniform(-1074, 1023), q.sign() * q.logUniform(-1074, 1023)],
  ]);
}

// ── Output ────────────────────────────────────────────────────────────────

function write(name, records) {
  const width = records[0].length;
  const view = new DataView(new ArrayBuffer(records.length * width * 8));
  records.forEach((rec, i) => rec.forEach((v, j) => view.setFloat64((i * width + j) * 8, v, true)));
  const file = join(OUT_DIR, `${name}.bin.gz`);
  writeFileSync(file, gzipSync(new Uint8Array(view.buffer), { level: 9 }));
  console.log(`${name}: ${records.length} records -> ${file}`);
}

const unary = (inputs, f) => inputs.map((x) => [x, f(x)]);
const binary = (pairs, f) => pairs.map(([a, b]) => [a, b, f(a, b)]);

write('sin', unary(trigInputs(1), Math.sin));
write('cos', unary(trigInputs(2), Math.cos));
write('tan', unary(trigInputs(3), Math.tan));
write('atan', unary(atanInputs(4), Math.atan));
write('exp', unary(expInputs(5), Math.exp));
write('log', unary(logInputs(6), Math.log));
write('atan2', binary(atan2Pairs(7), Math.atan2));
write('pow', binary(powPairs(8), Math.pow));
console.log(`node ${process.version}, v8 ${process.versions.v8}`);
