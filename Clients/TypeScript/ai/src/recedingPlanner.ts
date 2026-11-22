/**
 * An anytime receding-horizon planner — mirror of C# `Altruist.Gaming.RecedingPlanner<TCand, TFrame>`.
 * Candidates are parameterised input programs; each is played forward by the game (a rollout on a
 * scratch copy of the simulation) and scored, a few per tick within a budget. The best one runs as
 * exact per-tick inputs and is re-scored from the current tick every few ticks; a better one
 * replaces it. When the queue runs dry, the neighbours of the best candidate so far are queued (a
 * local refinement), a set number of rounds. The game decides when to restart (a new situation),
 * how candidates turn into frames, how a rollout scores, and when a plan is executed.
 *
 * `search` runs, in this order: the running plan is re-scored (when due and the budget allows, one
 * unit) → queued candidates in order: frames → a candidate whose presses fell before now is skipped
 * (no unit) → evaluate (one unit) → best so far (strictly greater) → noise draw → it replaces the
 * plan when `score + noise > plan.score + plan.noise + switchMargin` → once the queue is empty, the
 * best one's neighbours are queued (up to `refineRounds` times). Deterministic: the only draws are
 * the caller's `noise`, one per evaluated candidate.
 */

/** The plan being executed. */
export interface PlannedPlan<TCand, TFrame> {
  cand: TCand;
  /** Per-tick inputs from `start`. */
  frames: TFrame[];
  /** Tick of `frames[0]`. */
  start: number;
  score: number;
  /** Tick it was last scored. */
  checked: number;
  /** Score noise drawn for it: near-equal plans win unpredictably. */
  noise: number;
  /** Its first press was executed. */
  committed: boolean;
  /** Tick of the first press. */
  commitTick?: number;
}

/** What the game supplies to {@link RecedingPlanner.search}. Mirrors C# `IPlannerOps<TCand, TFrame>`. */
export interface PlannerOps<TCand, TFrame> {
  /** Tick the candidate's program starts (`frames(c)[0]`). */
  start(c: TCand): number;
  /** The candidate's per-tick inputs from its start, long enough to be scored at `tick`. */
  frames(c: TCand, tick: number): TFrame[];
  /** True when the frame presses a button (a press before now never happened: the candidate is stale). */
  pressed(f: TFrame): boolean;
  /** Plays `frames` from index `offset` (the current tick) and scores the outcome. */
  evaluate(c: TCand, frames: TFrame[], offset: number): number;
  /** One noise draw (scaled by the caller). */
  noise(): number;
  /** Neighbouring candidates for the refinement. */
  neighbours(c: TCand): TCand[];
  /** A better plan replaced one whose first press already ran (it continues the same attempt). */
  onReplan?(previous: PlannedPlan<TCand, TFrame>): void;
}

/** Settings of a {@link RecedingPlanner}. Mirrors the C# constructor parameters. */
export interface PlannerSettings {
  /** Ticks between re-scorings of the running plan. */
  recheckTicks: number;
  /** Score (with noise) a new candidate must beat the running plan by. */
  switchMargin: number;
  /** Refinement rounds around the best candidate per restart. */
  refineRounds: number;
}

/** A re-scoring is due at once (the plan was just changed or slid). */
const DUE = -1e9;

/**
 * Anytime receding-horizon planner over parameterised input programs. Mirrors C#
 * `RecedingPlanner<TCand, TFrame>`.
 * @typeParam TCand - A candidate (a maneuver with its parameters).
 * @typeParam TFrame - One tick of inputs.
 */
export class RecedingPlanner<TCand, TFrame> {
  /** The plan being executed (null: none). */
  plan: PlannedPlan<TCand, TFrame> | null = null;
  /** Best candidate of this pass (null before the first evaluation). */
  best: { cand: TCand; score: number } | null = null;
  /** Tick of the last restart. */
  epochTick = DUE;
  /** Refinement rounds done this pass. */
  refined = 0;
  readonly settings: PlannerSettings;
  private queue: TCand[] = [];

  constructor(settings: PlannerSettings) {
    this.settings = settings;
  }

  /** Candidates still waiting to be evaluated. */
  get pending(): number {
    return this.queue.length;
  }

  /** A new pass: these candidates, no best yet, the running plan re-scored at once. */
  restart(tick: number, candidates: readonly TCand[]): void {
    this.epochTick = tick;
    this.queue = [...candidates];
    this.best = null;
    this.refined = 0;
    if (this.plan) this.plan.checked = DUE;
  }

  /** Takes a plan found elsewhere (e.g. searched in full ahead of time) as a finished pass. */
  adopt(cand: TCand, frames: TFrame[], tick: number, score: number): void {
    this.plan = { cand, frames, start: tick, score, checked: tick, committed: false, noise: 0 };
    this.epochTick = tick;
    this.queue = [];
    this.best = { cand, score };
    this.refined = this.settings.refineRounds;
  }

  /** Spends up to `budget` evaluations; returns the units left. */
  search(tick: number, budget: number, ops: PlannerOps<TCand, TFrame>): number {
    const s = this.settings;
    const ep = this.plan;
    if (ep && budget > 0 && tick - ep.checked >= s.recheckTicks) {
      ep.score = ops.evaluate(ep.cand, ep.frames, tick - ep.start);
      ep.checked = tick;
      budget--;
    }
    while (budget > 0 && this.queue.length) {
      const c = this.queue.shift()!;
      const all = ops.frames(c, tick);
      const off = tick - ops.start(c);
      let stale = false;
      for (let i = 0; i < off && !stale; i++) if (ops.pressed(all[i]!)) stale = true;
      if (stale) continue;
      const score = ops.evaluate(c, all, off);
      budget--;
      if (!this.best || score > this.best.score) this.best = { cand: c, score };
      const noise = ops.noise();
      if (!this.plan || score + noise > this.plan.score + this.plan.noise + s.switchMargin) {
        const prev = this.plan;
        this.plan = { cand: c, frames: all, start: ops.start(c), score, checked: tick, committed: !!prev?.committed, commitTick: prev?.commitTick, noise };
        if (prev?.committed) ops.onReplan?.(prev);
      }
    }
    if (!this.queue.length && this.best && this.refined < s.refineRounds) {
      this.queue = ops.neighbours(this.best.cand);
      this.refined++;
    }
    return budget;
  }

  /** The search is done, the plan is a sure thing (`sure`), or the pass is `wait` ticks old. */
  settled(tick: number, sure: number, wait: number): boolean {
    const p = this.plan;
    return (this.queue.length === 0 && this.refined >= this.settings.refineRounds) || (!!p && p.score >= sure) || tick - this.epochTick >= wait;
  }

  /** Holds the plan back a tick (its inputs one tick later), re-scored next time. */
  slide(): void {
    if (!this.plan) return;
    this.plan.start++;
    this.plan.checked = DUE;
  }

  /** Marks the plan's first press as executed; true the first time. */
  commit(tick: number): boolean {
    const p = this.plan;
    if (!p || p.committed) return false;
    p.committed = true;
    p.commitTick = tick;
    return true;
  }

  /** Drops the plan; returns it. */
  drop(): PlannedPlan<TCand, TFrame> | null {
    const p = this.plan;
    this.plan = null;
    return p;
  }
}
