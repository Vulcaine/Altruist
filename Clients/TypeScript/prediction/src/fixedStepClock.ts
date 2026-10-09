/**
 * Fixed-step clock — TypeScript twin of C# `Altruist.Gaming.FixedStepClock` and `OverrunPolicy`.
 * The same frame-time sequence gives the same step counts, clamped times, overrun flags and
 * accumulator values bit for bit (verified against C# golden vectors in
 * `tests/golden/fixed-step-clock.json`).
 */

import { stepCutoff } from '../../input/src/inputSequencer.ts';

/** What a fixed-step clock does when a frame needs more steps than `maxStepsPerFrame`. Mirrors C#
 * `OverrunPolicy` (`Drop`, `Carry`, `SlowMotion`); the string values are the config spellings. */
export const OverrunPolicy = {
  /** The time left over after the last allowed step is discarded (the accumulator restarts at 0).
   * Best for clients: after a hitch the game resumes at once instead of fast-forwarding. */
  Drop: 'drop',
  /** The backlog is kept and stepped in the following frames (bounded by `maxFrameDelta`). */
  Carry: 'carry',
  /** A frame never adds more than `maxStepsPerFrame` steps of time: an overloaded loop runs slower
   * than real time without a backlog; the sub-step remainder is kept. */
  SlowMotion: 'slow-motion',
} as const;
/** One of the {@link OverrunPolicy} values. */
export type OverrunPolicy = (typeof OverrunPolicy)[keyof typeof OverrunPolicy];

/** Parses an overrun config value: `drop`, `carry` or `slow-motion` (case-insensitive, `-`/`_`
 * ignored; empty or missing = drop). Throws on anything else. Mirrors C# `FixedStepClock.ParseOverrun`. */
export function parseOverrun(value: string | null | undefined): OverrunPolicy {
  const v = (value ?? '').trim().replace(/[-_]/g, '').toLowerCase();
  if (v === '' || v === 'drop') return OverrunPolicy.Drop;
  if (v === 'carry') return OverrunPolicy.Carry;
  if (v === 'slowmotion') return OverrunPolicy.SlowMotion;
  throw new Error(`Unknown fixed-step overrun policy '${value}'. Use drop, carry or slow-motion.`);
}

/** Construction options of a {@link FixedStepClock} (C# constructor parameters). */
export interface FixedStepClockOptions {
  /** Longest frame time (seconds) counted per `advance`; longer frames are clamped. Default 0.25. */
  maxFrameDelta?: number;
  /** Most steps one frame may produce. Default 8. */
  maxStepsPerFrame?: number;
  /** Policy for time beyond `maxStepsPerFrame`. Default {@link OverrunPolicy.Drop}. */
  overrun?: OverrunPolicy;
}

/**
 * Fixed-timestep accumulator: turns variable frame times (seconds) into a whole number of equal
 * steps. The frame time is clamped to `maxFrameDelta`, added to the accumulator, and every whole
 * {@link dt} in it is one step, at most `maxStepsPerFrame` per frame. Mirrors C# `FixedStepClock`
 * exactly, including `dt = Math.fround(1 / hz)` (C# `1f / hz` widened to double), so a client loop
 * and a server room step identically for the same frame times.
 *
 * Use it for every client fixed-step loop (offline sessions, prediction: {@link PredictedSession}
 * uses one); pair it with {@link runFixedSteps} to sample input per step with catch-up cutoffs.
 * Render interpolation factor: {@link alpha}. Pure arithmetic, no clock of its own.
 * @example
 * ```ts
 * const clock = new FixedStepClock(60, { maxStepsPerFrame: 6 });
 * const steps = clock.advance(frameSeconds);
 * for (let i = 0; i < steps; i++) sim.step(input);
 * render(lerp(prev, curr, clock.alpha));
 * ```
 */
export class FixedStepClock {
  /** Steps per second. C# `Hz`. */
  readonly hz: number;
  /** Step length in seconds every step gets: `Math.fround(1 / hz)` (C# `Dt`, a float). */
  readonly dt: number;
  /** Longest frame time (seconds) one advance counts. C# `MaxFrameDelta`. */
  readonly maxFrameDelta: number;
  /** Most steps one frame may produce. C# `MaxStepsPerFrame`. */
  readonly maxStepsPerFrame: number;
  /** Policy for time beyond `maxStepsPerFrame`. C# `Overrun`. */
  readonly overrun: OverrunPolicy;
  /** Time not yet stepped (seconds). C# `Accumulator`. */
  accumulator = 0;
  /** Steps produced since construction. C# `TotalSteps`. */
  totalSteps = 0;
  /** The last advance's frame time after the clamp (C# `out clampedSeconds`). */
  lastClamped = 0;
  /** Whether the last advance hit `maxStepsPerFrame` (C# `out overrun`). */
  lastOverrun = false;

  /** A clock of `hz` steps per second (positive integer). Throws on a non-positive argument, like C#. */
  constructor(hz: number, options: FixedStepClockOptions = {}) {
    const maxFrameDelta = options.maxFrameDelta ?? 0.25;
    const maxSteps = options.maxStepsPerFrame ?? 8;
    if (!(hz > 0) || !Number.isInteger(hz)) throw new RangeError('The fixed step rate must be a positive integer.');
    if (!(maxFrameDelta > 0)) throw new RangeError('max-frame-delta must be positive.');
    if (!(maxSteps > 0) || !Number.isInteger(maxSteps)) throw new RangeError('max-steps-per-frame must be positive.');
    this.hz = hz;
    this.dt = Math.fround(1 / hz);
    this.maxFrameDelta = maxFrameDelta;
    this.maxStepsPerFrame = maxSteps;
    this.overrun = options.overrun ?? OverrunPolicy.Drop;
  }

  /**
   * Adds one frame's time and returns how many steps to run for it. C# `Advance(frameSeconds, out
   * clamped, out overrun)` (outs in {@link lastClamped} / {@link lastOverrun}).
   * @param frameSeconds Real time since the previous frame; negative counts as 0.
   * @param scale TypeScript only: game seconds per real second applied after the clamp (time scale,
   * input pacing such as `InputPacer.pace`). 1 (default) is exactly the C# arithmetic.
   */
  advance(frameSeconds: number, scale = 1): number {
    const clamped = frameSeconds < 0 ? 0 : frameSeconds > this.maxFrameDelta ? this.maxFrameDelta : frameSeconds;
    this.lastClamped = clamped;
    let add = scale === 1 ? clamped : clamped * scale;
    if (this.overrun === OverrunPolicy.SlowMotion) add = Math.min(add, this.dt * this.maxStepsPerFrame);
    this.accumulator += add;
    let steps = 0;
    while (this.accumulator >= this.dt && steps < this.maxStepsPerFrame) {
      this.accumulator -= this.dt;
      steps++;
    }
    const overrun = steps === this.maxStepsPerFrame;
    this.lastOverrun = overrun;
    if (overrun) {
      if (this.overrun === OverrunPolicy.Drop) this.accumulator = 0;
      else if (this.overrun === OverrunPolicy.Carry) this.accumulator = Math.min(this.accumulator, this.maxFrameDelta);
      else if (this.accumulator >= this.dt) this.accumulator %= this.dt;
    }
    this.totalSteps += steps;
    return steps;
  }

  /** Render interpolation factor between the last two steps: `min(1, accumulator / dt)` (0..1). */
  get alpha(): number {
    return Math.min(1, this.accumulator / this.dt);
  }

  /** Forgets the time not yet stepped. C# `Reset`. */
  reset(): void {
    this.accumulator = 0;
  }
}

/**
 * Advances `clock` by one frame and runs its steps, handing each step the input cutoff of its slice
 * of time ({@link stepCutoff}): catch-up steps after a hitch take only the input made during their
 * own slice; the newest step takes everything (`undefined`). Returns the step count.
 *
 * The client's per-frame loop: replaces hand-written `acc += dt; while (acc >= dt && steps < max)`
 * loops. Remaining time per step is computed from the accumulator after the frame (with `Drop`
 * overrun the dropped time no longer counts).
 * @param nowMs Wall time of the frame, ms (`performance.now()`).
 * @param step Runs one step; `index` 0..steps-1.
 * @example
 * ```ts
 * runFixedSteps(clock, frameSeconds, timeScale, performance.now(), (until) => sim.step(sequencer.next(now, until)));
 * ```
 */
export function runFixedSteps(
  clock: FixedStepClock,
  frameSeconds: number,
  scale: number,
  nowMs: number,
  step: (until: number | undefined, index: number, steps: number) => void,
): number {
  const steps = clock.advance(frameSeconds, scale);
  for (let i = 0; i < steps; i++) {
    const after = clock.accumulator + (steps - 1 - i) * clock.dt;
    step(stepCutoff(nowMs, after, clock.dt, scale, i < clock.maxStepsPerFrame - 1), i, steps);
  }
  return steps;
}
