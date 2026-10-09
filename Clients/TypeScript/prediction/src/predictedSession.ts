/**
 * Client-side prediction with server reconciliation for one local player in an Altruist room.
 */

import { InputPacer, InputSendQueue, type InputPacerOptions, type InputPacket, type InputSendQueueOptions } from '../../input/src/inputSendQueue.ts';
import { CorrectionSmoother, type BodyPose, type CorrectionOffset, type CorrectionOptions } from './correction.ts';
import { FixedStepClock, runFixedSteps, type FixedStepClockOptions } from './fixedStepClock.ts';

/** What the session needs to know about a snapshot (read it from your snapshot type in
 * {@link PredictionHooks.header}). Field names follow the room host's snapshot contract. */
export interface SnapshotHeader {
  /** Server step of the snapshot; newer wins. */
  tick: number;
  /** The server's `InputBuffer.LastAppliedSeq` for this player: inputs up to it are in the snapshot. */
  ackSeq: number;
  /** The server's `InputBuffer.Depth` for this player (our inputs still queued); omit if not sent. */
  inputDepth?: number;
  /** The server's `InputBuffer.TargetDepth`; omit if not sent. */
  inputTarget?: number;
}

/**
 * The game's side of a {@link PredictedSession}. The session owns timing, sequencing, rewind and
 * replay; the hooks own the game.
 */
export interface PredictionHooks<TSim, TInput, TSnap, TFrame, TEvent> {
  /** Tick, ack and input-depth fields of a snapshot. */
  header(snap: TSnap): SnapshotHeader;
  /** Resets the simulation to the authoritative snapshot (the rewind). */
  applySnapshot(sim: TSim, snap: TSnap): void;
  /** Runs one fixed step with the local player's input (remote players: repeat their last known
   * input inside your sim). Returned events are reported by `update` for forward steps only —
   * replayed steps' events are discarded (they already happened). */
  step(sim: TSim, input: TInput): readonly TEvent[] | void;
  /** Captures what is drawn (positions, angles, ...) after a step. */
  capture(sim: TSim): TFrame;
  /** Interpolates two captured frames (`t` 0..1). */
  lerpFrame(a: TFrame, b: TFrame, t: number): TFrame;
  /** The local input for the next step (sample your `InputSequencer` with `until`), already
   * quantized like the wire format so prediction steps exactly what the server will. */
  sampleInput(until: number | undefined): TInput;
  /** Sends one input packet (matches `InputBuffer.OfferRange(lastSeq, inputs)` on the server). */
  send(packet: InputPacket<TInput>): void;
  /** The world jumped between two applied snapshots (respawn, round reset): snap, don't glide.
   * Default: never (the first snapshot always snaps). */
  isTeleport?(prev: TSnap, next: TSnap): boolean;
  /** Called once per applied snapshot with the previously applied one (null for the first), before
   * the rewind: derive authoritative events (scores, round ends) here. */
  onSnapshot?(prev: TSnap | null, snap: TSnap): void;
  /** The poses of a captured frame for correction smoothing. Omit to disable smoothing. */
  bodies?(frame: TFrame): Iterable<BodyPose>;
  /** Adds an object's correction offset to a frame being drawn (mutate `frame`). Required for
   * smoothing to show. */
  applyOffset?(frame: TFrame, id: number | string, offset: CorrectionOffset): void;
}

/** Tuning of a {@link PredictedSession}. */
export interface PredictedSessionOptions {
  /** Fixed step rate (must match the server room). */
  hz: number;
  /** Clock tuning; default `{ maxFrameDelta: 0.25, maxStepsPerFrame: 8, overrun: 'drop' }`. DriftLink
   * uses `maxStepsPerFrame: 6`. */
  clock?: FixedStepClockOptions;
  /** Pending / redundancy tuning (default 120 pending, no redundancy). */
  inputs?: InputSendQueueOptions;
  /** Input pacing tuning, or false to run the input clock at real time. */
  pacing?: InputPacerOptions | false;
  /** Correction smoothing tuning. */
  correction?: CorrectionOptions;
  /** Wall clock in ms (default `performance.now()`); tests pass a fake. */
  now?: () => number;
}

/**
 * Online play with client-side prediction and server reconciliation, generic over the game:
 *
 * - every fixed step the local input is sampled, numbered, sent (with redundancy) and applied to
 *   the local simulation at once;
 * - only the newest snapshot matters: on the next `update` the simulation is reset to it, inputs the
 *   server has not applied (`ackSeq`) are replayed, and the visual jump is smoothed out
 *   ({@link CorrectionSmoother});
 * - the input clock runs slightly faster or slower ({@link InputPacer}) to keep the server's queue of
 *   our inputs at its target depth, so the server never starves or merges and predictions hold.
 *
 * Use it for a client predicting its own player against an Altruist room (`InputBuffer` on the
 * server). Offline or host-side play needs only a {@link FixedStepClock} and {@link runFixedSteps};
 * objects you do not predict can be drawn from an {@link InterpolationBuffer}. Deterministic given
 * the hooks, frame times and `now`. Units: frame times in seconds, `now` in ms, depths in steps.
 * Generic port of DriftLink's `NetSession` (with `LocalSession`'s step loop and `session.ts` lerp).
 * @example
 * ```ts
 * const session = new PredictedSession(sim, hooks, { hz: 60 });
 * socket.onSnapshot((s) => session.receive(s));
 * function frame(dt: number) { const events = session.update(dt); draw(session.frame()); }
 * ```
 */
export class PredictedSession<TSim, TInput, TSnap, TFrame, TEvent = never> {
  /** The predicted simulation. */
  readonly sim: TSim;
  /** The fixed-step clock (its `alpha` interpolates rendering). */
  readonly clock: FixedStepClock;
  /** Sequencing and pending inputs. */
  readonly inputs: InputSendQueue<TInput>;
  /** Input pacing (pace 1 when disabled). */
  readonly pacer: InputPacer;
  /** Correction offsets. */
  readonly correction: CorrectionSmoother;
  /** Largest position correction of the last reconciliation (world units). */
  lastCorrection = 0;
  /** Snapshots applied so far. */
  appliedCount = 0;

  private readonly hooks: PredictionHooks<TSim, TInput, TSnap, TFrame, TEvent>;
  private readonly pacing: boolean;
  private readonly now: () => number;
  private latest: TSnap | null = null;
  private latestTick = -Infinity;
  private applied: TSnap | null = null;
  private appliedTick = -Infinity;
  private prev: TFrame;
  private curr: TFrame;

  constructor(sim: TSim, hooks: PredictionHooks<TSim, TInput, TSnap, TFrame, TEvent>, options: PredictedSessionOptions) {
    this.sim = sim;
    this.hooks = hooks;
    this.clock = new FixedStepClock(options.hz, options.clock);
    this.inputs = new InputSendQueue<TInput>(options.inputs);
    this.pacing = options.pacing !== false;
    this.pacer = new InputPacer(options.pacing === false ? {} : options.pacing);
    this.correction = new CorrectionSmoother(options.correction);
    this.now = options.now ?? (() => (globalThis as unknown as { performance: { now(): number } }).performance.now());
    this.curr = hooks.capture(sim);
    this.prev = this.curr;
  }

  /** A snapshot arrived. Only the newest counts (older or equal ticks than the newest received or
   * applied one are ignored: late and reordered snapshots never rewind time); it is applied on the
   * next {@link update}. */
  receive(snap: TSnap): void {
    const tick = this.hooks.header(snap).tick;
    if (tick <= this.appliedTick || tick <= this.latestTick) return;
    this.latest = snap;
    this.latestTick = tick;
  }

  /** The current input clock rate (1 = real time). */
  get pace(): number {
    return this.pacing ? this.pacer.pace : 1;
  }

  /** The last applied snapshot (null before the first). */
  get lastApplied(): TSnap | null {
    return this.applied;
  }

  /**
   * Advances by one rendered frame of `frameSeconds`: reconciles with the newest snapshot (if any),
   * runs the due fixed steps (sample, send, predict), decays correction offsets. Returns the events
   * of the forward steps.
   */
  update(frameSeconds: number): TEvent[] {
    if (this.latest !== null) this.reconcile();
    const events: TEvent[] = [];
    const pace = this.pace;
    runFixedSteps(this.clock, frameSeconds, pace, this.now(), (until) => {
      const input = this.hooks.sampleInput(until);
      this.inputs.push(input);
      this.hooks.send(this.inputs.packet());
      this.prev = this.curr;
      const ev = this.hooks.step(this.sim, input);
      if (ev) for (const e of ev) events.push(e);
      this.curr = this.hooks.capture(this.sim);
    });
    this.correction.decay(frameSeconds);
    return events;
  }

  /** The frame to draw: the last two steps interpolated by the clock's alpha, plus correction
   * offsets (through {@link PredictionHooks.applyOffset}). */
  frame(): TFrame {
    const f = this.hooks.lerpFrame(this.prev, this.curr, this.clock.alpha);
    const apply = this.hooks.applyOffset;
    if (apply) for (const [id, o] of this.correction.entries()) apply(f, id, o);
    return f;
  }

  /** Forgets snapshots, pending inputs, pacing and offsets; sequences restart at 1 (pair with a
   * server-side `InputBuffer.Reset`: rejoin, new match). The simulation is not touched. */
  reset(): void {
    this.latest = null;
    this.latestTick = -Infinity;
    this.applied = null;
    this.appliedTick = -Infinity;
    this.inputs.reset();
    this.pacer.reset();
    this.correction.clear();
    this.clock.reset();
    this.curr = this.hooks.capture(this.sim);
    this.prev = this.curr;
  }

  private reconcile(): void {
    const snap = this.latest!;
    this.latest = null;
    const h = this.hooks.header(snap);
    if (h.inputDepth !== undefined) this.pacer.observe(h.inputDepth, h.inputTarget);
    const prevApplied = this.applied;
    const teleport = prevApplied === null || (this.hooks.isTeleport?.(prevApplied, snap) ?? false);
    this.hooks.onSnapshot?.(prevApplied, snap);
    this.applied = snap;
    this.appliedTick = h.tick;
    this.appliedCount++;
    const before = this.hooks.bodies ? this.hooks.capture(this.sim) : null;

    this.hooks.applySnapshot(this.sim, snap);
    this.inputs.ack(h.ackSeq);
    for (const p of this.inputs.pending) this.hooks.step(this.sim, p.input);

    const after = this.hooks.capture(this.sim);
    if (before !== null) this.lastCorrection = this.correction.correct(this.hooks.bodies!(before), this.hooks.bodies!(after), teleport);
    this.curr = after;
    this.prev = after;
  }
}
