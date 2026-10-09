/**
 * Render-side correction smoothing: after a prediction is corrected (rewind + replay), the drawn
 * objects keep their old place through an offset that decays to zero, so they glide instead of
 * jumping. Purely visual: the simulation itself always holds the corrected state.
 */

/** Wraps an angle difference to [-π, π] with `a - 2π·round(a / 2π)` (radians). */
export function wrapAngleDelta(a: number): number {
  return a - 2 * Math.PI * Math.round(a / (2 * Math.PI));
}

/**
 * Shrinks a position offset vector (any dimension, world units) in place over `dt` seconds:
 * exponential decay at `rate` (1/s), but never faster than `maxSpeed` units/s unless `minRate`
 * alone shrinks it faster (large offsets still go within a bounded time). With `maxSpeed` infinite
 * this is plain `o *= exp(-rate·dt)`.
 * @example
 * ```ts
 * decayOffset(offset, 14, frameSeconds, 12, 4);
 * ```
 */
export function decayOffset(o: number[], rate: number, dt: number, maxSpeed = Infinity, minRate = 4): void {
  let sq = 0;
  for (const v of o) sq += v * v;
  const len = Math.sqrt(sq);
  if (len < 1e-6) return;
  const step = Math.min(len * (1 - Math.exp(-rate * dt)), Math.max(maxSpeed * dt, len * (1 - Math.exp(-minRate * dt))));
  const f = (len - step) / len;
  for (let i = 0; i < o.length; i++) o[i]! *= f;
}

/** Scales a position offset vector in place so its length is at most `max` (world units). */
export function clampOffset(o: number[], max: number): void {
  let sq = 0;
  for (const v of o) sq += v * v;
  const len = Math.sqrt(sq);
  if (len <= max) return;
  for (let i = 0; i < o.length; i++) o[i]! *= max / len;
}

/** One drawn object's pose as the correction sees it (from your captured render frame). */
export interface BodyPose {
  /** Stable id (player id, entity id, a fixed name for the ball). */
  id: number | string;
  /** Position, any dimension, world units. */
  pos: readonly number[];
  /** Rotation, radians (omit for objects without one). */
  angle?: number;
  /** Snap instead of glide for this object (respawned, demolished, hidden) — checked in both the
   * before and after pose. */
  snap?: boolean;
  /** Decay rate (1/s) of this object's offset; default {@link CorrectionOptions.rate}. Remote
   * objects predicted from stale inputs usually look better slower (e.g. 7 against a local 14). */
  rate?: number;
}

/** The visual offset of one object: add it to the drawn pose. */
export interface CorrectionOffset {
  /** Position offset (same dimension as the pose), world units. */
  pos: number[];
  /** Angle offset, radians. */
  angle: number;
  /** Decay rate in use (1/s). */
  rate: number;
}

/** Tuning of a {@link CorrectionSmoother}. Rates are generic; the distance limits depend on world
 * scale, so they default to off. */
export interface CorrectionOptions {
  /** Default decay rate (1/s): ~1/rate seconds to fade. Default 14. */
  rate?: number;
  /** Fastest an offset moves the drawn object (units/s), unless `minRate` is faster. Default
   * Infinity (plain exponential decay). */
  maxSpeed?: number;
  /** Slowest decay rate (1/s) when `maxSpeed` limits. Default 4. */
  minRate?: number;
  /** Offsets longer than this are cut back (a desync that large is better shown than chased).
   * Default Infinity. */
  maxOffset?: number;
}

/**
 * Per-object visual correction offsets. {@link correct} after each reconciliation with the poses
 * before and after it; {@link decay} once per rendered frame; add {@link get}'s offset to what you
 * draw. Use it whenever predicted objects are corrected by authoritative snapshots; for remote
 * objects you only interpolate ({@link InterpolationBuffer}) no smoothing is needed.
 * @example
 * ```ts
 * const before = poses(capture(sim)); reconcile(); const after = poses(capture(sim));
 * smoother.correct(before, after, teleport);
 * // each frame:
 * smoother.decay(frameSeconds);
 * const o = smoother.get(id); if (o) { x += o.pos[0]; y += o.pos[1]; a += o.angle; }
 * ```
 */
export class CorrectionSmoother {
  private readonly offsets = new Map<number | string, CorrectionOffset>();
  private readonly o: Required<CorrectionOptions>;

  constructor(options: CorrectionOptions = {}) {
    this.o = {
      rate: options.rate ?? 14,
      maxSpeed: options.maxSpeed ?? Infinity,
      minRate: options.minRate ?? 4,
      maxOffset: options.maxOffset ?? Infinity,
    };
  }

  /**
   * Adds the jump of every object present in both pose sets (before minus after) to its offset; a
   * teleport (or a pose flagged `snap`) zeroes it instead. Objects gone from `after` lose their
   * offset. Returns the largest position correction (world units) of this call.
   */
  correct(before: Iterable<BodyPose>, after: Iterable<BodyPose>, teleport: boolean): number {
    const prev = new Map<number | string, BodyPose>();
    for (const p of before) prev.set(p.id, p);
    const seen = new Set<number | string>();
    let maxErr = 0;
    for (const p of after) {
      seen.add(p.id);
      const q = prev.get(p.id);
      if (q === undefined) continue;
      let o = this.offsets.get(p.id);
      if (o === undefined) {
        o = { pos: new Array<number>(p.pos.length).fill(0), angle: 0, rate: this.o.rate };
        this.offsets.set(p.id, o);
      }
      o.rate = p.rate ?? this.o.rate;
      let sq = 0;
      for (let i = 0; i < p.pos.length; i++) {
        const d = (q.pos[i] ?? 0) - p.pos[i]!;
        sq += d * d;
      }
      if (teleport || p.snap || q.snap) {
        o.pos.fill(0);
        o.angle = 0;
      } else {
        for (let i = 0; i < p.pos.length; i++) o.pos[i] = (o.pos[i] ?? 0) + ((q.pos[i] ?? 0) - p.pos[i]!);
        if (p.angle !== undefined && q.angle !== undefined) o.angle = wrapAngleDelta(o.angle + wrapAngleDelta(q.angle - p.angle));
        clampOffset(o.pos, this.o.maxOffset);
      }
      maxErr = Math.max(maxErr, Math.sqrt(sq));
    }
    for (const id of [...this.offsets.keys()]) if (!seen.has(id)) this.offsets.delete(id);
    return maxErr;
  }

  /** Decays every offset over one rendered frame of `dt` seconds. */
  decay(dt: number): void {
    for (const o of this.offsets.values()) {
      decayOffset(o.pos, o.rate, dt, this.o.maxSpeed, this.o.minRate);
      o.angle *= Math.exp(-o.rate * dt);
    }
  }

  /** The offset of `id` (undefined: none). Add it to the drawn pose. */
  get(id: number | string): CorrectionOffset | undefined {
    return this.offsets.get(id);
  }

  /** Every offset by id. */
  entries(): IterableIterator<[number | string, CorrectionOffset]> {
    return this.offsets.entries();
  }

  /** Drops every offset (rejoin, scene change). */
  clear(): void {
    this.offsets.clear();
  }
}
