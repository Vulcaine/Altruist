/**
 * Client side of the sequenced-input contract of C# `InputBuffer<TInput>`: sequence numbers, the
 * redundant packet window (`InputBuffer.OfferRange`), acks (`InputBuffer.LastAppliedSeq`) and
 * input-depth pacing (`InputBuffer.Depth` / `TargetDepth`).
 */

/** One sent input and its sequence number. */
export interface SequencedInput<T> {
  /** Sequence number (1, 2, ...; 0 means none). */
  seq: number;
  /** The input as sent (already quantized like the wire format). */
  input: T;
}

/** What to send for one step: the newest `inputs.length` inputs, oldest first, the last having
 * sequence `lastSeq` — exactly the arguments of C# `InputBuffer.OfferRange(lastSeq, inputs)`. */
export interface InputPacket<T> {
  /** Sequence of the newest input (the last element of `inputs`). */
  lastSeq: number;
  /** Inputs oldest first; repeats the previous ones for redundancy against packet loss. */
  inputs: T[];
}

/** Tuning of an {@link InputSendQueue}. */
export interface InputSendQueueOptions {
  /** Unacknowledged inputs kept for replay; older ones are dropped (a desync that long is resolved
   * by the snapshot anyway). Default 120 (2 s at 60 Hz). */
  maxPending?: number;
  /** Inputs per packet (1: no redundancy; 2-4 survive isolated losses on unreliable transports).
   * Default 1. */
  redundancy?: number;
}

/**
 * Numbers the local player's inputs, keeps those the server has not applied yet (for prediction
 * replay), and builds packets for the server's `InputBuffer.OfferRange`. One per local player.
 *
 * Contract with C# `InputBuffer<TInput>`: sequences start at 1 and grow by one per step; the
 * server's snapshot ack is its `LastAppliedSeq` (which may count a speculative step: the input is
 * then applied as far as the server can tell, so it leaves the pending list too); after a server
 * `Reset` (rejoin, new match) call {@link reset} so both restart at 1.
 *
 * Used by {@link PredictedSession} (in `@altruist/prediction`); use it directly when you write your
 * own prediction loop. Pure bookkeeping, no clock.
 * @example
 * ```ts
 * const q = new InputSendQueue<Pad>({ redundancy: 3 });
 * q.push(input);
 * const p = q.packet();            // send p.lastSeq + p.inputs
 * // on snapshot:
 * q.ack(snapshot.ackSeq);
 * for (const { input } of q.pending) sim.step(input);   // replay
 * ```
 */
export class InputSendQueue<T> {
  private readonly _pending: SequencedInput<T>[] = [];
  private readonly history: T[] = [];
  private readonly maxPending: number;
  private readonly redundancy: number;
  /** Sequence of the newest input pushed (0 before the first). */
  lastSeq = 0;
  /** Newest ack seen (the server's `LastAppliedSeq`). */
  lastAck = 0;

  constructor(options: InputSendQueueOptions = {}) {
    this.maxPending = Math.max(1, options.maxPending ?? 120);
    this.redundancy = Math.max(1, options.redundancy ?? 1);
  }

  /** Numbers `input` with the next sequence and keeps it pending; returns the sequence. */
  push(input: T): number {
    const seq = ++this.lastSeq;
    this._pending.push({ seq, input });
    if (this._pending.length > this.maxPending) this._pending.shift();
    this.history.push(input);
    if (this.history.length > this.redundancy) this.history.shift();
    return seq;
  }

  /** The packet for the newest input: up to `redundancy` newest inputs (sent ones, acked or not). */
  packet(): InputPacket<T> {
    return { lastSeq: this.lastSeq, inputs: [...this.history] };
  }

  /** The server applied every input up to `seq`: drops them from {@link pending}. Older acks
   * (reordered snapshots) are ignored. */
  ack(seq: number): void {
    if (seq < this.lastAck) return;
    this.lastAck = seq;
    let n = 0;
    while (n < this._pending.length && this._pending[n]!.seq <= seq) n++;
    if (n > 0) this._pending.splice(0, n);
  }

  /** Inputs sent but not acknowledged, oldest first (replay them after a rewind). */
  get pending(): readonly SequencedInput<T>[] {
    return this._pending;
  }

  /** Forgets everything; the next input is sequence 1 (pair with the server's `InputBuffer.Reset`). */
  reset(): void {
    this._pending.length = 0;
    this.history.length = 0;
    this.lastSeq = 0;
    this.lastAck = 0;
  }
}

/** Tuning of an {@link InputPacer}; defaults are DriftLink's (a 60 Hz action game), all in steps or
 * fractions, so they suit most 30-120 Hz rooms. */
export interface InputPacerOptions {
  /** Smallest depth (inputs) kept queued on the server on a steady link. Default 2. */
  minDepth?: number;
  /** Largest depth the pacer ever aims for (bounds input latency). Default 5. */
  maxDepth?: number;
  /** Pace change per input of depth error (0.02: 2 % faster per missing input). Default 0.02. */
  gain?: number;
  /** Largest pace deviation from 1 (0.05: the input clock runs 0.95x..1.05x). Default 0.05. */
  maxPace?: number;
  /** Exponential smoothing factor per report for depth and depth jitter (0..1). Default 0.1. */
  smoothing?: number;
}

/**
 * Input-clock pacing toward the server's target queue depth. The server consumes one input per
 * step and reports how many of ours are still queued (`InputBuffer.Depth`) and how many it wants
 * (`InputBuffer.TargetDepth`). An empty queue makes it repeat our last input (a misprediction), an
 * overfull one merges or drops; so the client's step clock runs up to `maxPace` faster or slower to
 * keep `target` queued: max(minDepth, server target, 1 + 2 x measured depth jitter), capped by
 * maxDepth.
 *
 * Multiply the client's fixed-step frame time by {@link pace} (e.g. `FixedStepClock.advance(dt,
 * pacer.pace)`). Feed {@link observe} once per received snapshot. {@link PredictedSession} does
 * both. Generic port of DriftLink's `NetSession` pacing (identical arithmetic).
 * @example
 * ```ts
 * pacer.observe(snap.inputDepth, snap.inputTarget);
 * clock.advance(frameSeconds, pacer.pace);
 * ```
 */
export class InputPacer {
  private readonly o: Required<InputPacerOptions>;
  /** Smoothed reported depth (inputs). */
  depthAvg: number;
  /** Smoothed absolute deviation of the reported depth (inputs). */
  depthJitter = 0;
  /** The depth aimed for at the last report (inputs). */
  target: number;
  /** Input clock rate (1 = real time). */
  pace = 1;

  constructor(options: InputPacerOptions = {}) {
    this.o = {
      minDepth: options.minDepth ?? 2,
      maxDepth: options.maxDepth ?? 5,
      gain: options.gain ?? 0.02,
      maxPace: options.maxPace ?? 0.05,
      smoothing: options.smoothing ?? 0.1,
    };
    this.depthAvg = this.o.minDepth;
    this.target = this.o.minDepth;
  }

  /** One server report: `depth` = our inputs still queued there; `serverTarget` = its
   * `TargetDepth` (omit when not sent). Updates {@link pace}. */
  observe(depth: number, serverTarget?: number): void {
    const o = this.o;
    this.depthJitter += (Math.abs(depth - this.depthAvg) - this.depthJitter) * o.smoothing;
    this.depthAvg += (depth - this.depthAvg) * o.smoothing;
    this.target = Math.min(o.maxDepth, Math.max(o.minDepth, serverTarget ?? 0, 1 + 2 * this.depthJitter));
    this.pace = 1 + Math.max(-o.maxPace, Math.min(o.maxPace, (this.target - this.depthAvg) * o.gain));
  }

  /** Back to the initial state (pace 1). */
  reset(): void {
    this.depthAvg = this.o.minDepth;
    this.depthJitter = 0;
    this.target = this.o.minDepth;
    this.pace = 1;
  }
}
