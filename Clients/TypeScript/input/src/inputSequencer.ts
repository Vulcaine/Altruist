/**
 * Client input sequencing: device state changes (with timestamps) in, one canonical input per fixed
 * step out, keeping every press and the order of presses as declared by a {@link RoomInputModel}.
 * Client-only (no C# twin): it produces the stream the server's `InputBuffer` and `RoomInputModel`
 * expect.
 */

import type { RoomInputModel } from './roomInputModel.ts';

/** A device state and when it happened. `t` is wall time in milliseconds (`performance.now()`). */
export interface TimedInput<T> {
  /** Wall time of the change, ms. */
  t: number;
  /** The full device state after the change (axes already quantized, held-buttons bitmask). */
  state: T;
}

/** Tuning of an {@link InputSequencer}. */
export interface InputSequencerOptions {
  /** Changes older than this (ms) when a step first sees them only set the held state: a tap that is
   * long over is gone. A safety net; pauses are caught by {@link PauseDetector}. Default 2000. */
  staleMs?: number;
  /** A backlog this long means nothing samples the sequencer: it resyncs. Default 256. */
  maxQueue?: number;
  /**
   * Held (non-action) bits that qualify a press rather than stand on their own, such as an aim
   * direction that only a dash press reads ({@link ButtonField} bits). When such a bit rises after
   * an earlier press of the same step, the change can still join that step, so a press arriving
   * together with its qualifier is not pushed a step later just because another button was pressed
   * first. Releasing a qualifier bit after a press still waits, like any release. Default 0: every
   * held-bit change after a press waits for the next step.
   */
  pressQualifiers?: number;
}

/**
 * Turns a device's timestamped state changes into one input per simulation step, keeping every
 * press and the order of presses (the model's stage order):
 *
 * - changes apply in the order they happened; a tap shorter than a step (press and release, even
 *   1 ms apart) still shows as held for exactly one step;
 * - a step takes presses only in stage order, one per stage; a press that would break that (a
 *   second press of a stage, a lower stage after a higher one) and everything after it waits for the
 *   next step — never merged away or dropped;
 * - after a press nothing else of that step changes (no axis or other held-field move, no release):
 *   a stick flicked 2 ms after a jump belongs to the next step;
 * - a button released and pressed again within a step is released for one step, then pressed;
 * - two presses seen in the same change are split in stage order.
 *
 * So every output satisfies {@link RoomInputModel.isCanonical} against the previous one. Non-action
 * bits of the button word (held buttons, {@link ButtonField} values such as a direction index) are
 * held state: they apply together with the change they arrived in.
 *
 * Use it per local player between the device layer and the fixed step; feed device changes with
 * {@link push} (or several devices through a {@link StateMerger}), sample once per step with
 * {@link next}. Deterministic and clock-free apart from the times passed in. A held bit that changes
 * after a press waits for the next step, unless it is a rising `pressQualifiers` bit (an aim
 * direction that arrives with a later press of the same step).
 * @example
 * ```ts
 * const seq = new InputSequencer(model, { x: 0, buttons: 0 });
 * device.onChange((state) => seq.push({ t: performance.now(), state }));
 * // in the fixed step:
 * const input = seq.next(performance.now(), cutoff);
 * ```
 */
export class InputSequencer<T> {
  private queue: TimedInput<T>[] = [];
  private out: T;
  private latest: T;
  private readonly model: RoomInputModel<T>;
  private readonly staleMs: number;
  private readonly maxQueue: number;
  private readonly qualifiers: number;

  /**
   * @param model The room input model (must declare buttons: {@link RoomInputModel.hasButtons}).
   * @param neutral The idle device state (what the simulation holds before any change).
   */
  constructor(model: RoomInputModel<T>, neutral: T, options: InputSequencerOptions = {}) {
    if (!model.hasButtons) throw new Error('InputSequencer needs a model that declares buttons(get, with).');
    this.model = model;
    this.out = neutral;
    this.latest = neutral;
    this.staleMs = options.staleMs ?? 2000;
    this.maxQueue = options.maxQueue ?? 256;
    this.qualifiers = (options.pressQualifiers ?? 0) & ~model.actionMask;
  }

  /** A state change. Repeats of the newest state are ignored. Changes must be pushed in time order. */
  push(change: TimedInput<T>): void {
    if (this.model.equals(change.state, this.latest)) return;
    this.latest = change.state;
    this.queue.push(change);
    if (this.queue.length > this.maxQueue) this.resync();
  }

  /** What the device holds now (the newest pushed state). */
  get held(): T {
    return this.latest;
  }

  /** What the last {@link next} handed out (what the simulation holds now). */
  get current(): T {
    return this.out;
  }

  /** Changes waiting for a later step. */
  get pending(): number {
    return this.queue.length;
  }

  /** Drops queued changes: the next step is what the device holds now. Call after a pause or menu
   * ({@link PauseDetector}). */
  resync(): void {
    this.queue = [];
    this.out = this.latest;
  }

  /**
   * The next step's input.
   * @param now Current wall time, ms (changes older than `staleMs` only set the held state).
   * @param until Only changes up to this wall time (ms) count; later ones wait (catch-up steps after
   * a hitch each take their own slice of time: {@link stepCutoff}). Default: everything.
   */
  next(now: number, until = Infinity): T {
    const m = this.model;
    while (this.queue.length > 0 && this.queue[0]!.t < now - this.staleMs) this.out = this.queue.shift()!.state;
    let f = this.out;
    let fB = m.buttonsOf(f);
    const action = m.actionMask;
    let lastStage = -1;
    let pressed = 0;
    let released = 0;
    while (this.queue.length > 0) {
      const s = this.queue[0]!;
      if (s.t > until) break;
      const sB = m.buttonsOf(s.state);
      const releases = fB & ~sB;
      const risingQualifiers = sB & ~fB & this.qualifiers;
      const held = !m.equals(m.withButtons(s.state, 0), m.withButtons(f, 0)) || ((fB ^ sB) & ~action & ~risingQualifiers) !== 0;
      // After a press nothing else changes this step (the press keeps its context).
      if (pressed !== 0 && (held || releases !== 0)) break;
      f = s.state;
      fB &= sB;
      released |= releases;
      let rising = sB & ~fB;
      fB |= rising & ~action;
      rising &= action;
      for (const b of m.order) {
        if ((rising & b) === 0) continue;
        const stage = m.stageOf(b);
        // Out of order, a second press of a stage, or released earlier this step: next step.
        if (stage <= lastStage || (released & b) !== 0) break;
        fB |= b;
        pressed |= b;
        lastStage = stage;
      }
      if (fB !== sB) break; // presses left over: the change stays queued
      this.queue.shift();
    }
    this.out = m.withButtons(f, fB);
    return this.out;
  }
}

/** One device's changes for {@link StateMerger.merge}. */
export interface DeviceChanges<T> {
  /** Stable device id (keyboard, gamepad index). */
  id: string;
  /** Its changes since the last merge, in time order. */
  changes: readonly TimedInput<T>[];
  /** What it holds now (a newest change that differs gets a change at `now`). */
  current: T;
}

/**
 * Largest-magnitude axis per field plus the OR of every held button: the usual way to combine
 * several devices of one player (keyboard + gamepad). Returns a combine function for
 * {@link StateMerger}; `axes` are numeric fields, `buttons` the bitmask field.
 * @example
 * ```ts
 * const merger = new StateMerger(combineLargestAxes<Pad>(['x', 'y'], 'buttons', neutral), model.equals.bind(model), neutral);
 * ```
 */
export function combineLargestAxes<T extends object>(axes: readonly (keyof T)[], buttons: keyof T, neutral: T): (states: readonly T[]) => T {
  return (states) => {
    const out = { ...neutral } as Record<keyof T, unknown>;
    for (const a of axes) out[a] = 0;
    let b = 0;
    for (const s of states) {
      for (const a of axes) if (Math.abs(s[a] as number) > Math.abs(out[a] as number)) out[a] = s[a];
      b |= s[buttons] as number;
    }
    out[buttons] = b;
    return out as T;
  };
}

/**
 * Merges the change streams of several devices of one player into one stream in time order, using
 * a game-supplied `combine` (e.g. {@link combineLargestAxes}). Feed its output to an
 * {@link InputSequencer}. Use it whenever one player may hold more than one device; with a single
 * device push its changes straight into the sequencer.
 */
export class StateMerger<T> {
  private readonly states = new Map<string, T>();
  private combined: T;
  private readonly combine: (states: readonly T[]) => T;
  private readonly equals: (a: T, b: T) => boolean;
  private readonly neutral: T;

  /**
   * @param combine The combined state of every known device (insertion order).
   * @param equals State equality (use the model's: `(a, b) => model.equals(a, b)`).
   * @param neutral The state of no device.
   */
  constructor(combine: (states: readonly T[]) => T, equals: (a: T, b: T) => boolean, neutral: T) {
    this.combine = combine;
    this.equals = equals;
    this.neutral = neutral;
    this.combined = neutral;
  }

  /**
   * Folds the sources' changes into combined changes, in time order (ties: source order). A source
   * whose newest change differs from its `current` gets a change at `now` (missed events); a source
   * no longer listed is dropped (change at `now`). `now` in ms.
   */
  merge(sources: readonly DeviceChanges<T>[], now: number): TimedInput<T>[] {
    const all: { id: string; s: TimedInput<T>; order: number }[] = [];
    let order = 0;
    for (const src of sources) for (const s of src.changes) all.push({ id: src.id, s, order: order++ });
    all.sort((a, b) => a.s.t - b.s.t || a.order - b.order);
    const out: TimedInput<T>[] = [];
    const emit = (t: number) => {
      const c = this.combine([...this.states.values()]);
      if (this.equals(c, this.combined)) return;
      this.combined = c;
      out.push({ t, state: c });
    };
    for (const e of all) {
      this.states.set(e.id, e.s.state);
      emit(e.s.t);
    }
    const ids = new Set(sources.map((s) => s.id));
    for (const id of [...this.states.keys()]) {
      if (ids.has(id)) continue;
      this.states.delete(id);
      emit(now);
    }
    for (const src of sources) {
      const known = this.states.get(src.id) ?? this.neutral;
      if (!this.equals(known, src.current)) {
        this.states.set(src.id, src.current);
        emit(now);
      }
    }
    return out;
  }
}

/** Counts rendered frames; the clock of {@link PauseDetector}. Call {@link mark} once per rendered
 * frame (also while menus are open). */
export class FrameCounter {
  /** Frames marked so far. */
  frame = 0;

  /** One rendered frame passed. */
  mark(): void {
    this.frame++;
  }
}

/**
 * True on the first sample after the game stopped sampling for more than `pauseFrames` rendered
 * frames (or the very first sample): the caller resyncs its {@link InputSequencer}, dropping taps
 * made in a menu. Counts rendered frames rather than time, so a hitch (no frames at all) never drops
 * a press.
 * @example
 * ```ts
 * if (pause.resumed()) sequencer.resync();
 * ```
 */
export class PauseDetector {
  private last = -1;
  private readonly frames: FrameCounter;
  private readonly pauseFrames: number;

  /** @param pauseFrames Rendered frames without a sample that count as a pause. Default 8. */
  constructor(frames: FrameCounter, pauseFrames = 8) {
    this.frames = frames;
    this.pauseFrames = pauseFrames;
  }

  /** Call on every sample; true when sampling resumed after a pause. */
  resumed(): boolean {
    const f = this.frames.frame;
    const resumed = this.last < 0 || f - this.last > this.pauseFrames;
    this.last = f;
    return resumed;
  }
}

/**
 * The `until` (wall ms) for one fixed step of a frame that runs several (a hitch): when that step's
 * slice of game time ends, so each catch-up step takes the input made during its own slice. The
 * newest step (less than one more step left, or no more steps allowed) takes everything:
 * `undefined`.
 * @param now Wall time of the frame, ms.
 * @param accAfter Game seconds still unsimulated after this step.
 * @param dt Step length, seconds.
 * @param scale Game seconds per wall second (time scale, input pace).
 * @param moreSteps Whether the frame may still run another step.
 */
export function stepCutoff(now: number, accAfter: number, dt: number, scale: number, moreSteps: boolean): number | undefined {
  if (!moreSteps || accAfter < dt || scale <= 0) return undefined;
  return now - (accAfter / scale) * 1000;
}
