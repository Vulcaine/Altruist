/**
 * Flow layer. Utility-based choice between a fixed set of options — mirror of C#
 * `UtilitySelector<TOption, TCtx>`. `decide` runs, in this order: forced rules (first true decides,
 * no scoring, no draws) → prepare → scores in option order (veto ⇒ -Infinity, the veto sees the raw
 * score) → noise in option order for every noisy option, vetoed ones included
 * (`score += draw(rng) * scale`, default draw `rng.gaussian()`, scale read once) → inertia on the
 * current option unless transient (`(tick - since) * tickSeconds < hold ? heldBonus : bonus`) →
 * best: start from the current option (the fallback while the current one is transient), walk the
 * options in order, take strictly greater scores → overrides in order. `decide` does not change
 * the current option; `choose` (or `select`) does.
 */

/** The noise source (a `DeterministicRandom` satisfies it). */
export interface NoiseSource {
  gaussian(): number;
}

/** Read access to the scores of the decision in progress. */
export interface UtilityScores<TOption> {
  /** Score by option. */
  of(option: TOption): number;
  /** Score by index (option order). */
  at(index: number): number;
  readonly count: number;
}

export type UtilityOverride<TOption, TCtx> = (ctx: TCtx, best: TOption, scores: UtilityScores<TOption>) => TOption;

interface OptionDef<TOption, TCtx> {
  option: TOption;
  score: (ctx: TCtx) => number;
  veto: ((ctx: TCtx, score: number) => boolean) | null;
  noisy: boolean;
  transient: boolean;
}

export interface OptionSettings<TCtx> {
  /** True rules the option out (score -Infinity); sees the raw score. */
  veto?: (ctx: TCtx, score: number) => boolean;
  /** Noise is added (default true). */
  noisy?: boolean;
  /** Entered only by forced rules or the game: no inertia; while current, the best-pick walk starts from the fallback. */
  transient?: boolean;
}

export class UtilitySelector<TOption, TCtx, TRng = NoiseSource> {
  private readonly options: readonly TOption[];
  private readonly defs: readonly OptionDef<TOption, TCtx>[];
  private readonly forced: readonly { name: string; when: (ctx: TCtx) => boolean; option: TOption }[];
  private readonly prepare: ((ctx: TCtx) => void) | null;
  private readonly noiseScale: ((ctx: TCtx) => number) | null;
  private readonly draw: (rng: TRng) => number;
  private readonly hold: ((ctx: TCtx) => number) | null;
  private readonly heldBonus: number;
  private readonly bonus: number;
  private readonly tickSeconds: number;
  private readonly fallback: number;
  private readonly overrides: readonly { name: string; rule: UtilityOverride<TOption, TCtx> }[];
  private readonly u: number[];
  private readonly scoresView: UtilityScores<TOption>;

  /** Index of the current choice. */
  currentIndex: number;
  /** Index of the choice before the current one. */
  previousIndex: number;
  /** Tick at which the current choice was made. */
  since = 0;
  /** Name of the forced rule that decided the last `decide`, or null when it was scored. */
  lastForced: string | null = null;

  /** @internal Use `UtilitySelector.create()`. */
  constructor(b: UtilitySelectorBuilder<TOption, TCtx, TRng>) {
    if (b.defs.length === 0) throw new Error('A utility selector needs at least one option.');
    this.defs = [...b.defs];
    this.options = this.defs.map((d) => d.option);
    this.forced = [...b.forced];
    this.prepare = b.prepareHook;
    this.noiseScale = b.noiseScale;
    this.draw = b.drawFn ?? ((r: TRng) => (r as unknown as NoiseSource).gaussian());
    this.hold = b.holdFn;
    this.heldBonus = b.heldBonus;
    this.bonus = b.bonus;
    this.tickSeconds = b.tickSeconds;
    this.fallback = b.hasFallback ? this.indexOf(b.fallbackOption as TOption) : -1;
    this.overrides = [...b.overrides];
    this.u = new Array<number>(this.defs.length).fill(0);
    this.currentIndex = b.hasInitial ? this.indexOf(b.initialOption as TOption) : 0;
    this.previousIndex = this.currentIndex;
    const u = this.u;
    this.scoresView = {
      of: (o: TOption) => u[this.indexOf(o)]!,
      at: (i: number) => u[i]!,
      get count() {
        return u.length;
      },
    };
  }

  static create<TOption, TCtx, TRng = NoiseSource>(): UtilitySelectorBuilder<TOption, TCtx, TRng> {
    return new UtilitySelectorBuilder<TOption, TCtx, TRng>();
  }

  get current(): TOption {
    return this.options[this.currentIndex] as TOption;
  }

  get previous(): TOption {
    return this.options[this.previousIndex] as TOption;
  }

  /** The options in their fixed order. */
  get optionList(): readonly TOption[] {
    return this.options;
  }

  /** The scores of the last scored decision. */
  get scores(): UtilityScores<TOption> {
    return this.scoresView;
  }

  indexOf(option: TOption): number {
    const i = this.options.indexOf(option);
    if (i < 0) throw new Error(`'${String(option)}' is not an option of this selector.`);
    return i;
  }

  /** Runs the stages and returns the best option (does not change `current`). */
  decide(ctx: TCtx, rng: TRng, tick: number): TOption {
    for (const f of this.forced) {
      if (!f.when(ctx)) continue;
      this.lastForced = f.name;
      return f.option;
    }
    this.lastForced = null;
    this.prepare?.(ctx);
    const u = this.u;
    const defs = this.defs;
    const n = u.length;
    for (let i = 0; i < n; i++) {
      const d = defs[i]!;
      let s = d.score(ctx);
      if (d.veto && d.veto(ctx, s)) s = -Infinity;
      u[i] = s;
    }
    if (this.noiseScale) {
      const scale = this.noiseScale(ctx);
      for (let i = 0; i < n; i++) if (defs[i]!.noisy) u[i] = u[i]! + this.draw(rng) * scale;
    }
    const cur = this.currentIndex;
    if (this.hold && !defs[cur]!.transient) {
      const held = (tick - this.since) * this.tickSeconds < this.hold(ctx);
      u[cur] = u[cur]! + (held ? this.heldBonus : this.bonus);
    }
    let best = defs[cur]!.transient && this.fallback >= 0 ? this.fallback : cur;
    for (let i = 0; i < n; i++) if (u[i]! > u[best]!) best = i;
    let pick: TOption = this.options[best] as TOption;
    for (const o of this.overrides) pick = o.rule(ctx, pick, this.scoresView);
    return pick;
  }

  /** Makes `option` current at `tick`; false (nothing changes) when it already is. */
  choose(option: TOption, tick: number): boolean {
    const i = this.indexOf(option);
    if (i === this.currentIndex) return false;
    this.previousIndex = this.currentIndex;
    this.currentIndex = i;
    this.since = tick;
    return true;
  }

  /** `decide` then `choose`. */
  select(ctx: TCtx, rng: TRng, tick: number): { option: TOption; changed: boolean } {
    const option = this.decide(ctx, rng, tick);
    return { option, changed: this.choose(option, tick) };
  }

  /** Restores the bookkeeping (rollback, replays, a reset). */
  restore(current: TOption, previous: TOption, since: number): void {
    this.currentIndex = this.indexOf(current);
    this.previousIndex = this.indexOf(previous);
    this.since = since;
  }
}

export class UtilitySelectorBuilder<TOption, TCtx, TRng = NoiseSource> {
  /** @internal */ readonly defs: OptionDef<TOption, TCtx>[] = [];
  /** @internal */ readonly forced: { name: string; when: (ctx: TCtx) => boolean; option: TOption }[] = [];
  /** @internal */ readonly overrides: { name: string; rule: UtilityOverride<TOption, TCtx> }[] = [];
  /** @internal */ prepareHook: ((ctx: TCtx) => void) | null = null;
  /** @internal */ noiseScale: ((ctx: TCtx) => number) | null = null;
  /** @internal */ drawFn: ((rng: TRng) => number) | null = null;
  /** @internal */ holdFn: ((ctx: TCtx) => number) | null = null;
  /** @internal */ heldBonus = 0;
  /** @internal */ bonus = 0;
  /** @internal */ tickSeconds = 0;
  /** @internal */ hasFallback = false;
  /** @internal */ fallbackOption: TOption | undefined = undefined;
  /** @internal */ hasInitial = false;
  /** @internal */ initialOption: TOption | undefined = undefined;

  /** Adds an option (call order = option order). `score` may be a constant. */
  option(option: TOption, score: number | ((ctx: TCtx) => number), settings: OptionSettings<TCtx> = {}): this {
    if (this.defs.some((d) => d.option === option)) throw new Error(`Option '${String(option)}' was added twice.`);
    const fn = typeof score === 'number' ? () => score : score;
    this.defs.push({ option, score: fn, veto: settings.veto ?? null, noisy: settings.noisy ?? true, transient: settings.transient ?? false });
    return this;
  }

  /** A forced rule (checked in order before any scoring). */
  force(name: string, when: (ctx: TCtx) => boolean, option: TOption): this {
    this.forced.push({ name, when, option });
    return this;
  }

  /** Runs once per scored decision, before the scorers. */
  prepare(fn: (ctx: TCtx) => void): this {
    this.prepareHook = fn;
    return this;
  }

  /** Noise on noisy options: `score += draw(rng) * scale(ctx)` (default draw: `rng.gaussian()`). */
  noise(scale: (ctx: TCtx) => number, draw?: (rng: TRng) => number): this {
    this.noiseScale = scale;
    this.drawFn = draw ?? null;
    return this;
  }

  /** Hysteresis: `heldBonus` while `(tick - since) * tickSeconds < hold(ctx)`, `bonus` after. */
  inertia(hold: (ctx: TCtx) => number, heldBonus: number, bonus: number, tickSeconds: number): this {
    this.holdFn = hold;
    this.heldBonus = heldBonus;
    this.bonus = bonus;
    this.tickSeconds = tickSeconds;
    return this;
  }

  /** Where the best-pick walk starts while the current option is transient. */
  fallbackTo(option: TOption): this {
    this.hasFallback = true;
    this.fallbackOption = option;
    return this;
  }

  /** The current option before any choice (default: the first option). */
  startWith(option: TOption): this {
    this.hasInitial = true;
    this.initialOption = option;
    return this;
  }

  /** An override rule, applied in order after the best pick. */
  override(name: string, rule: UtilityOverride<TOption, TCtx>): this {
    this.overrides.push({ name, rule });
    return this;
  }

  build(): UtilitySelector<TOption, TCtx, TRng> {
    return new UtilitySelector(this);
  }
}
