/**
 * Input buffer — TypeScript twin of C# `Altruist.Gaming.Rooms.InputBuffer<TInput>` and
 * `InputBufferOptions` / `InputBufferStats`. The same offers and consumes give the same inputs,
 * acks, depths, target depths and counters (verified against C# golden vectors in
 * `tests/golden/input-buffer.json`).
 *
 * The server owns the real one. Use this twin to run the server's buffering on the client: offline
 * or host-side sessions that should behave like online ones, link simulators and tests of client
 * pacing. A client that only sends inputs needs {@link InputSendQueue} and {@link InputPacer}.
 */

import type { RoomInputModel } from './roomInputModel.ts';
import { RoomInputModel as Model } from './roomInputModel.ts';

/** Tuning of an {@link InputBuffer}; mirrors C# `InputBufferOptions` (same defaults, suiting 30-120 Hz
 * rooms). All counts are in inputs or steps. */
export interface InputBufferOptions {
  /** Inputs waiting beyond this are merged (or, when that would lose a press, dropped oldest first).
   * Default 16. */
  maxQueued?: number;
  /** Lower bound of the adaptive target depth (steps). Default 0. */
  minTarget?: number;
  /** Upper bound of the adaptive target depth; also the depth a standing queue must stay above to be
   * drained. Default 2. */
  maxTarget?: number;
  /** Arrivals the jitter is measured over. Default 120. */
  jitterWindow?: number;
  /** Steps a queue must stay above `maxTarget` before it is drained. Default 30. */
  drainWindow?: number;
  /** Speculate on starved steps and drain standing queues (false: plain FIFO). Default true. */
  catchUp?: boolean;
  /** Starved steps in a row that stand in for the client's next inputs. Default 8. */
  maxSpeculation?: number;
}

/** Counters of one buffer; mirrors C# `InputBufferStats` (same meaning per field). */
export interface InputBufferStats {
  /** Inputs accepted (new sequence numbers, including the real input of a speculative step). */
  offered: number;
  /** Inputs dropped because their sequence was already applied. */
  stale: number;
  /** Inputs dropped because their sequence was already queued. */
  duplicates: number;
  /** Queued inputs applied to a step (merged pairs count once). */
  applied: number;
  /** Steps with nothing queued (the held state was repeated). */
  starved: number;
  /** Starved steps that stood in for the client's next input. */
  speculated: number;
  /** Speculative steps whose real input then arrived equal to what was applied. */
  speculationHits: number;
  /** Inputs folded into the next one (draining a standing queue, overflow). */
  merged: number;
  /** Inputs dropped on overflow because merging would have lost a press. */
  dropped: number;
  /** Presses (declared action edges) in the offered stream. */
  pressesOffered: number;
  /** Presses applied to the simulation. */
  pressesApplied: number;
  /** Offered presses that never reached the simulation. */
  pressesLost: number;
  /** Presses applied in a different order than offered. */
  pressesReordered: number;
  /** Presses applied that were never offered. */
  pressesExtra: number;
}

function newStats(): InputBufferStats {
  return {
    offered: 0, stale: 0, duplicates: 0, applied: 0, starved: 0, speculated: 0, speculationHits: 0,
    merged: 0, dropped: 0, pressesOffered: 0, pressesApplied: 0, pressesLost: 0, pressesReordered: 0, pressesExtra: 0,
  };
}

interface Queued<T> { seq: number; input: T }
interface Spec<T> { seq: number; input: T; wrong: boolean }

/**
 * A player's sequenced inputs between the network and the fixed step: one input per step, stale and
 * duplicate sequences dropped, starved steps repeat held state (never a press) and speculate on the
 * next sequence, standing queues are drained by press-safe merges, and the target depth follows the
 * measured arrival jitter. Mirrors C# `InputBuffer<TInput>` method for method (see its docs for the
 * full contract); `Totals` (process-wide sums) are C#-only.
 *
 * Sequence numbers start at 1 (0 means "nothing applied yet"). Time is counted in steps
 * ({@link consume} calls), never seconds. Deterministic.
 * @example
 * ```ts
 * const buf = new InputBuffer(neutral, model);
 * buf.offerRange(packet.lastSeq, packet.inputs);  // on arrival
 * const input = buf.consume();                    // once per fixed step
 * snapshot.ackSeq = buf.lastAppliedSeq; snapshot.inputDepth = buf.depth; snapshot.inputTarget = buf.targetDepth;
 * ```
 */
export class InputBuffer<T> {
  private readonly queue: Queued<T>[] = [];
  private readonly opt: Required<InputBufferOptions>;
  private readonly _model: RoomInputModel<T>;
  private readonly neutral: T;
  private readonly offsets: number[];
  private offsetCount = 0;
  private offsetNext = 0;
  private readonly depths: number[];
  private depthCount = 0;
  private depthNext = 0;
  private step = 0;
  private readonly spec: Spec<T>[] = [];
  private lastOffered: T;
  private readonly expected: { seq: number; bit: number }[] = [];
  private readonly scratch: number[] = [];

  /** Sequence of the input applied last (send it as the snapshot ack). C# `LastAppliedSeq`. */
  lastAppliedSeq = 0;
  /** Newest sequence queued. C# `LastQueuedSeq`. */
  lastQueuedSeq = 0;
  /** The input applied last (repeated while starved). C# `Last`. */
  last: T;
  /** How many inputs the client should keep queued here (steps). C# `TargetDepth`. */
  targetDepth: number;
  /** Arrival jitter over the window, in steps. C# `Jitter`. */
  jitter = 0;
  /** This buffer's counters. C# `Stats`. */
  readonly stats: InputBufferStats = newStats();

  /**
   * A buffer for one participant. C# `new InputBuffer(neutral, model, options)`.
   * @param neutral The input of an idle seat (the "last input" before anything arrived and after reset).
   * @param model What the input is; omitted treats inputs as opaque ({@link RoomInputModel.opaque}).
   * @param options Tuning; omitted fields use the C# defaults.
   */
  constructor(neutral: T, model?: RoomInputModel<T> | null, options?: InputBufferOptions | null) {
    this.neutral = neutral;
    this._model = model ?? Model.opaque<T>();
    const o = options ?? {};
    this.opt = {
      maxQueued: o.maxQueued ?? 16,
      minTarget: o.minTarget ?? 0,
      maxTarget: o.maxTarget ?? 2,
      jitterWindow: o.jitterWindow ?? 120,
      drainWindow: o.drainWindow ?? 30,
      catchUp: o.catchUp ?? true,
      maxSpeculation: o.maxSpeculation ?? 8,
    };
    this.offsets = new Array<number>(Math.max(2, this.opt.jitterWindow)).fill(0);
    this.depths = new Array<number>(Math.max(1, this.opt.drainWindow)).fill(0);
    this.last = neutral;
    this.lastOffered = neutral;
    this.targetDepth = this.opt.minTarget;
  }

  /** Inputs waiting (send it as the snapshot input depth). C# `Depth`. */
  get depth(): number {
    return this.queue.length;
  }

  /** Speculative steps not confirmed or corrected yet. C# `Speculating`. */
  get speculating(): number {
    return this.spec.length;
  }

  /** The input model in use. C# `Model`. */
  get model(): RoomInputModel<T> {
    return this._model;
  }

  /** Queues an input; false when it was stale or a duplicate. C# `Offer`. */
  offer(seq: number, input: T): boolean {
    if (seq <= this.lastAppliedSeq && this.spec.length > 0 && this.resolveSpeculation(seq, input)) return true;
    if (seq <= this.lastAppliedSeq || seq <= this.lastQueuedSeq) {
      if (seq <= this.lastAppliedSeq) this.stats.stale++;
      else this.stats.duplicates++;
      return false;
    }
    while (this.spec.length > 0 && this.spec[0]!.seq < seq) this.spec.shift();
    this.accept(seq, input);
    this.queue.push({ seq, input });
    this.lastQueuedSeq = seq;
    while (this.queue.length > this.opt.maxQueued) this.shrink();
    return true;
  }

  /** Queues a run of inputs ending at `lastSeq` (oldest first), as sent by
   * {@link InputSendQueue.packet}; already known ones are skipped. Returns how many were new. C#
   * `OfferRange`. */
  offerRange(lastSeq: number, inputs: readonly T[]): number {
    let added = 0;
    for (let i = 0; i < inputs.length; i++) {
      const seq = lastSeq - (inputs.length - 1 - i);
      if (seq > this.lastQueuedSeq && seq > this.lastAppliedSeq && this.offer(seq, inputs[i]!)) added++;
    }
    return added;
  }

  /** The input for this step: the next queued one (merged when catching up), or the held state
   * again. Call exactly once per fixed step. C# `Consume`. */
  consume(): T {
    this.step++;
    const prev = this.last;
    if (this.queue.length === 0) {
      this.stats.starved++;
      this.last = this._model.starved(prev);
      if (this.opt.catchUp && this.spec.length < this.opt.maxSpeculation && this.lastAppliedSeq > 0) {
        this.lastAppliedSeq++;
        this.spec.push({ seq: this.lastAppliedSeq, input: this.last, wrong: false });
        this.stats.speculated++;
      }
      this.recordDepth();
      this.track(prev, this.last, Number.MIN_SAFE_INTEGER);
      return this.last;
    }
    let item = this.queue.shift()!;
    if (this.opt.catchUp && this.queue.length > this.opt.maxTarget && this.standingAbove(this.opt.maxTarget)) item = this.tryFold(prev, item);
    this.stats.applied++;
    this.lastAppliedSeq = Math.max(this.lastAppliedSeq, item.seq);
    this.last = item.input;
    this.recordDepth();
    this.track(prev, this.last, item.seq);
    return this.last;
  }

  /** Forgets every queued input, speculation, jitter sample and sequence (sequences start again
   * from 1). Statistics are kept. C# `Reset`. */
  reset(): void {
    this.queue.length = 0;
    this.expected.length = 0;
    this.lastAppliedSeq = 0;
    this.lastQueuedSeq = 0;
    this.last = this.neutral;
    this.lastOffered = this.neutral;
    this.spec.length = 0;
    this.offsetCount = this.offsetNext = 0;
    this.depthCount = this.depthNext = 0;
    this.jitter = 0;
    this.targetDepth = this.opt.minTarget;
  }

  private accept(seq: number, input: T): void {
    this.stats.offered++;
    this.recordArrival(seq);
    const presses = this._model.presses(this.lastOffered, input);
    this.lastOffered = input;
    if (presses === 0) return;
    this.scratch.length = 0;
    this._model.appendInOrder(presses, this.scratch);
    for (const b of this.scratch) this.expected.push({ seq, bit: b });
    this.stats.pressesOffered += this.scratch.length;
  }

  private resolveSpeculation(seq: number, input: T): boolean {
    while (this.spec.length > 0 && this.spec[0]!.seq < seq) this.spec.shift();
    const node = this.spec[0];
    if (node === undefined || node.seq !== seq) return false;
    this.spec.shift();
    this.accept(seq, input);
    this.lastQueuedSeq = Math.max(this.lastQueuedSeq, seq);
    if (!node.wrong && this._model.equals(node.input, input)) {
      this.stats.speculationHits++;
      return true;
    }
    for (const s of this.spec) s.wrong = true;
    let at = 0;
    while (at < this.queue.length && this.queue[at]!.seq < seq) at++;
    this.queue.splice(at, 0, { seq, input });
    return true;
  }

  private tryFold(prev: T, item: Queued<T>): Queued<T> {
    const next = this.queue[0];
    if (next === undefined) return item;
    const merged = this._model.tryMerge(prev, item.input, next.input);
    if (merged === undefined) return item;
    this.queue.shift();
    this.stats.merged++;
    return { seq: next.seq, input: merged };
  }

  private shrink(): void {
    let prev = this.last;
    for (let i = 0; i + 1 < this.queue.length; i++) {
      const node = this.queue[i]!;
      const next = this.queue[i + 1]!;
      const merged = this._model.tryMerge(prev, node.input, next.input);
      if (merged !== undefined) {
        this.queue[i + 1] = { seq: next.seq, input: merged };
        this.queue.splice(i, 1);
        this.stats.merged++;
        return;
      }
      prev = node.input;
    }
    this.queue.shift();
    this.stats.dropped++;
  }

  private standingAbove(target: number): boolean {
    if (this.depthCount < this.depths.length) return false;
    for (let i = 0; i < this.depthCount; i++) if (this.depths[i]! <= target) return false;
    return true;
  }

  private recordDepth(): void {
    this.depths[this.depthNext] = this.queue.length;
    this.depthNext = (this.depthNext + 1) % this.depths.length;
    if (this.depthCount < this.depths.length) this.depthCount++;
  }

  private recordArrival(seq: number): void {
    this.offsets[this.offsetNext] = this.step - seq;
    this.offsetNext = (this.offsetNext + 1) % this.offsets.length;
    if (this.offsetCount < this.offsets.length) this.offsetCount++;
    let min = Number.MAX_VALUE;
    let max = -Number.MAX_VALUE;
    for (let i = 0; i < this.offsetCount; i++) {
      const o = this.offsets[i]!;
      if (o < min) min = o;
      if (o > max) max = o;
    }
    this.jitter = max - min;
    const t = Math.ceil(this.jitter - 0.5);
    this.targetDepth = Math.min(Math.max(t, this.opt.minTarget), this.opt.maxTarget);
  }

  private track(prev: T, applied: T, appliedSeq: number): void {
    const presses = this._model.presses(prev, applied);
    if (presses !== 0) {
      this.scratch.length = 0;
      this._model.appendInOrder(presses, this.scratch);
      this.stats.pressesApplied += this.scratch.length;
      for (const b of this.scratch) {
        if (this.expected.length > 0 && this.expected[0]!.bit === b) {
          this.expected.shift();
          continue;
        }
        const found = this.expected.findIndex((e) => e.bit === b);
        if (found < 0) this.stats.pressesExtra++;
        else {
          this.expected.splice(found, 1);
          this.stats.pressesReordered++;
        }
      }
    }
    while (this.expected.length > 0 && this.expected[0]!.seq <= appliedSeq) {
      this.expected.shift();
      this.stats.pressesLost++;
    }
  }
}
