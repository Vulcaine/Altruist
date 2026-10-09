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
  /** An approximately normal draw (see {@link DeterministicRandom.gaussian}). */
  gaussian(): number;
}

/** Read access to the scores of the decision in progress (a live view over the selector's buffer).
 * Mirrors the C# readonly struct `UtilityScores<TOption>`. */
export interface UtilityScores<TOption> {
  /** Score by option (throws for an unknown option). C# indexer `scores[option]`. */
  of(option: TOption): number;
  /** Score by index (option order). C# indexer `scores[index]`. */
  at(index: number): number;
  /** Number of options. C# `UtilityScores.Count`. */
  readonly count: number;
}

/** A post-pick rule: receives the best option so far and the scores, returns the option to use (the
 * same one to keep it). Must return one of the selector's options. Mirrors the C# delegate
 * `UtilityOverride<TOption, TCtx>`. */
export type UtilityOverride<TOption, TCtx> = (ctx: TCtx, best: TOption, scores: UtilityScores<TOption>) => TOption;

interface OptionDef<TOption, TCtx> {
  option: TOption;
  score: (ctx: TCtx) => number;
  veto: ((ctx: TCtx, score: number) => boolean) | null;
  noisy: boolean;
  transient: boolean;
}

/** Per-option settings of {@link UtilitySelectorBuilder.option}. Mirrors the optional parameters of C#
 * `Builder.Option(option, score, veto, noisy, transient)`. */
export interface OptionSettings<TCtx> {
  /** True rules the option out (score -Infinity); sees the raw score. */
  veto?: (ctx: TCtx, score: number) => boolean;
  /** Noise is added (default true). */
  noisy?: boolean;
  /** Entered only by forced rules or the game: no inertia; while current, the best-pick walk starts from the fallback. */
  transient?: boolean;
}

/**
 * Utility-based choice between a fixed set of options (an AI role, a stance, a target) with forced
 * rules, noise, hysteresis and overrides. Mirrors C# `UtilitySelector<TOption, TCtx>` (where the RNG
 * is always a `DeterministicRandom`). Deterministic given the same context, RNG state and tick.
 * Use {@link FirstMatch} instead when the choice is a strict priority list with no scoring.
 * @typeParam TOption - Option values (compared with `===`; strings or enum members work well).
 * @typeParam TCtx - What the scorers read.
 * @typeParam TRng - The RNG passed to `decide`; defaults to {@link NoiseSource}.
 * @example
 * ```ts
 * const role = UtilitySelector.create<Role, Ctx, DeterministicRandom>()
 *   .option('attack', (c) => c.attackScore)
 *   .option('defend', (c) => c.defendScore, { veto: (c) => c.isLastLine })
 *   .option('recover', 0, { transient: true, noisy: false })
 *   .force('kickoff', (c) => c.isKickoff, 'attack')
 *   .noise((c) => c.skillNoise)
 *   .inertia(() => 0.5, 0.2, 0.05, 1 / 60)
 *   .fallbackTo('defend')
 *   .build();
 * const { option, changed } = role.select(ctx, rng, tick);
 * ```
 */
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

  /** Index of the current choice. C# `UtilitySelector.CurrentIndex`. */
  currentIndex: number;
  /** Index of the choice before the current one. C# `UtilitySelector.PreviousIndex`. */
  previousIndex: number;
  /** Tick at which the current choice was made (0 initially). C# `UtilitySelector.Since`. */
  since = 0;
  /** Name of the forced rule that decided the last `decide`, or null when it was scored. C#
   * `UtilitySelector.LastForced`. */
  lastForced: string | null = null;

  /** @internal Use {@link UtilitySelector.create}. Throws when the builder has no option, or when
   * `fallbackTo` / `startWith` name an unknown option. */
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

  /** Starts a builder. Mirrors C# `UtilitySelector<TOption, TCtx>.Create`. */
  static create<TOption, TCtx, TRng = NoiseSource>(): UtilitySelectorBuilder<TOption, TCtx, TRng> {
    return new UtilitySelectorBuilder<TOption, TCtx, TRng>();
  }

  /** The current option. C# `UtilitySelector.Current`. */
  get current(): TOption {
    return this.options[this.currentIndex] as TOption;
  }

  /** The option before the current one. C# `UtilitySelector.Previous`. */
  get previous(): TOption {
    return this.options[this.previousIndex] as TOption;
  }

  /** The options in their fixed order. C# `UtilitySelector.Options`. */
  get optionList(): readonly TOption[] {
    return this.options;
  }

  /** The scores of the last scored decision (a live view; a forced decision leaves them unchanged).
   * C# `UtilitySelector.Scores`. */
  get scores(): UtilityScores<TOption> {
    return this.scoresView;
  }

  /** Index of `option` in option order; throws for an unknown option. C# `UtilitySelector.IndexOf`. */
  indexOf(option: TOption): number {
    const i = this.options.indexOf(option);
    if (i < 0) throw new Error(`'${String(option)}' is not an option of this selector.`);
    return i;
  }

  /** Runs the stages (see the file header: forced → prepare → scores / vetoes → noise → inertia → best
   * → overrides) and returns the best option. Does not change `current`; draws from `rng` once per
   * noisy option when noise is configured and no forced rule fired. C# `UtilitySelector.Decide`. */
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

  /** Makes `option` current at `tick` (previous ← current, since ← tick); false (nothing changes) when
   * it already is. Throws for an unknown option. C# `UtilitySelector.Choose`. */
  choose(option: TOption, tick: number): boolean {
    const i = this.indexOf(option);
    if (i === this.currentIndex) return false;
    this.previousIndex = this.currentIndex;
    this.currentIndex = i;
    this.since = tick;
    return true;
  }

  /** {@link decide} then {@link choose}: returns the option and whether it changed. C#
   * `UtilitySelector.Select` (returns `changed` via `out`). */
  select(ctx: TCtx, rng: TRng, tick: number): { option: TOption; changed: boolean } {
    const option = this.decide(ctx, rng, tick);
    return { option, changed: this.choose(option, tick) };
  }

  /** Restores the bookkeeping (rollback, replays, a reset) without deciding. C# `UtilitySelector.Restore`. */
  restore(current: TOption, previous: TOption, since: number): void {
    this.currentIndex = this.indexOf(current);
    this.previousIndex = this.indexOf(previous);
    this.since = since;
  }
}

/** Builder of a {@link UtilitySelector}; get one from {@link UtilitySelector.create}. Not an
 * {@link OrderedBuilder}: options, forced rules and overrides keep call order and cannot be edited by
 * name. Mirrors C# `UtilitySelector<TOption, TCtx>.Builder`. */
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

  /** Adds an option (call order = option order; throws on a duplicate). `score` may be a constant.
   * Mirrors both C# `Builder.Option` overloads. @returns this. */
  option(option: TOption, score: number | ((ctx: TCtx) => number), settings: OptionSettings<TCtx> = {}): this {
    if (this.defs.some((d) => d.option === option)) throw new Error(`Option '${String(option)}' was added twice.`);
    const fn = typeof score === 'number' ? () => score : score;
    this.defs.push({ option, score: fn, veto: settings.veto ?? null, noisy: settings.noisy ?? true, transient: settings.transient ?? false });
    return this;
  }

  /** Adds a forced rule (checked in order before any scoring; the first true decides with no draws).
   * C# `Builder.Force`. @returns this. */
  force(name: string, when: (ctx: TCtx) => boolean, option: TOption): this {
    this.forced.push({ name, when, option });
    return this;
  }

  /** Sets a hook run once per scored decision, before the scorers (cache shared inputs here). C#
   * `Builder.Prepare`. @returns this. */
  prepare(fn: (ctx: TCtx) => void): this {
    this.prepareHook = fn;
    return this;
  }

  /** Noise on noisy options: `score += draw(rng) * scale(ctx)` (default draw: `rng.gaussian()`; scale
   * read once per decision). C# `Builder.Noise`. @returns this. */
  noise(scale: (ctx: TCtx) => number, draw?: (rng: TRng) => number): this {
    this.noiseScale = scale;
    this.drawFn = draw ?? null;
    return this;
  }

  /** Hysteresis on the current (non-transient) option: `+heldBonus` while
   * `(tick - since) * tickSeconds < hold(ctx)` (hold in seconds), `+bonus` after. C# `Builder.Inertia`. @returns this. */
  inertia(hold: (ctx: TCtx) => number, heldBonus: number, bonus: number, tickSeconds: number): this {
    this.holdFn = hold;
    this.heldBonus = heldBonus;
    this.bonus = bonus;
    this.tickSeconds = tickSeconds;
    return this;
  }

  /** Where the best-pick walk starts while the current option is transient. C# `Builder.FallbackTo`. @returns this. */
  fallbackTo(option: TOption): this {
    this.hasFallback = true;
    this.fallbackOption = option;
    return this;
  }

  /** The current option before any choice (default: the first option). C# `Builder.StartWith`. @returns this. */
  startWith(option: TOption): this {
    this.hasInitial = true;
    this.initialOption = option;
    return this;
  }

  /** Adds an override rule, applied in order after the best pick. C# `Builder.Override`. @returns this. */
  override(name: string, rule: UtilityOverride<TOption, TCtx>): this {
    this.overrides.push({ name, rule });
    return this;
  }

  /** Creates the {@link UtilitySelector} (throws without options). C# `Builder.Build`. */
  build(): UtilitySelector<TOption, TCtx, TRng> {
    return new UtilitySelector(this);
  }
}
