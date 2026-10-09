/**
 * Flow layer. Ordered rule selectors — mirror of C# `FirstMatch<TCtx, TResult>` (here `FirstMatch`)
 * and `FirstMatch<TCtx>` (here `FirstMatchActions`): the replacement for `if / else if` ladders.
 * Rules run in build order; the first match decides and later rules are not evaluated. A `try`
 * rule may decline after looking closer (returns `NO_MATCH`; C#: `TryRule` returning false).
 * Every predicate up to the matching rule runs exactly once, in order.
 */
import { OrderedBuilder } from './orderedBuilder.ts';

/** Returned by a `try` rule to decline (the next rule is tried), and by
 * {@link FirstMatch.tryEvaluate} when nothing answered. Stands in for C#'s `TryRule` returning
 * false / `TryEvaluate` returning false. */
export const NO_MATCH: unique symbol = Symbol('NO_MATCH');
/** The type of {@link NO_MATCH}. */
export type NoMatch = typeof NO_MATCH;

interface Rule<TCtx, TResult> {
  when: ((ctx: TCtx) => boolean) | null;
  then: ((ctx: TCtx) => TResult) | null;
  value: TResult | undefined;
  hasValue: boolean;
  tryRule: ((ctx: TCtx) => TResult | NoMatch) | null;
}

/**
 * Picks a result: the first matching rule's, else the default's. Mirrors C#
 * `FirstMatch<TCtx, TResult>`. Use it to replace an `if / else if` ladder that computes a value; for
 * a ladder of side effects use {@link FirstMatchActions}; for weighted / scored choices with
 * hysteresis use {@link UtilitySelector}.
 * @example
 * ```ts
 * const pickMode = FirstMatch.create<Ctx, Mode>()
 *   .whenValue('stunned', (c) => c.stunTimer > 0, 'idle')
 *   .try('chase', (c) => (c.target ? 'chase' : NO_MATCH))
 *   .when('flee', (c) => c.health < 0.2, (c) => (c.canRun ? 'flee' : 'hide'))
 *   .otherwiseValue('patrol')
 *   .build();
 * const mode = pickMode.evaluate(ctx); // pickMode.nameOf(pickMode.lastRule) for debugging
 * ```
 */
export class FirstMatch<TCtx, TResult> {
  /** Index of the rule that answered the last evaluation (-1: the default, or nothing matched). C#
   * returns it through `TryEvaluate(ctx, out result, out ruleIndex)`. */
  lastRule = -1;

  private readonly ruleNames: readonly string[];
  private readonly rules: readonly Rule<TCtx, TResult>[];
  private readonly hasOtherwise: boolean;
  private readonly otherwise: ((ctx: TCtx) => TResult) | null;
  private readonly otherwiseValue: TResult | undefined;

  private constructor(
    ruleNames: readonly string[],
    rules: readonly Rule<TCtx, TResult>[],
    hasOtherwise: boolean,
    otherwise: ((ctx: TCtx) => TResult) | null,
    otherwiseValue: TResult | undefined,
  ) {
    this.ruleNames = ruleNames;
    this.rules = rules;
    this.hasOtherwise = hasOtherwise;
    this.otherwise = otherwise;
    this.otherwiseValue = otherwiseValue;
  }

  /** Starts a builder (rules are then added in evaluation order). Mirrors C# `FirstMatch<TCtx, TResult>.Create`. */
  static create<TCtx, TResult>(): FirstMatchBuilder<TCtx, TResult> {
    return new FirstMatchBuilder<TCtx, TResult>((n, r, h, o, v) => new FirstMatch(n, r, h, o, v));
  }

  /** Number of rules (the default not counted). C# `FirstMatch.Count`. */
  get count(): number {
    return this.rules.length;
  }

  /** True when a default answers if no rule matched. C# `FirstMatch.HasDefault`. */
  get hasDefault(): boolean {
    return this.hasOtherwise;
  }

  /** Name of the rule at `index` (e.g. `nameOf(lastRule)`); undefined at runtime for an out-of-range
   * index. C# `FirstMatch.NameOf`. */
  nameOf(index: number): string {
    return this.ruleNames[index]!;
  }

  /** Index of the rule named `name`, or -1. C# `FirstMatch.IndexOf`. */
  indexOf(name: string): number {
    return this.ruleNames.indexOf(name);
  }

  /** The first matching rule's result, else the default's; throws (`Error`) when nothing matched and
   * there is no default. Predicates run in order, each at most once, stopping at the match. Sets
   * {@link lastRule}. C# `FirstMatch.Evaluate`. */
  evaluate(ctx: TCtx): TResult {
    const r = this.tryEvaluate(ctx);
    if (r === NO_MATCH) throw new Error('No rule matched and there is no default.');
    return r;
  }

  /** The first matching rule's result, else the default's, else {@link NO_MATCH} (never throws). Sets
   * {@link lastRule}. C# `FirstMatch.TryEvaluate`. */
  tryEvaluate(ctx: TCtx): TResult | NoMatch {
    const rules = this.rules;
    for (let i = 0; i < rules.length; i++) {
      const r = rules[i]!;
      if (r.tryRule) {
        const out = r.tryRule(ctx);
        if (out !== NO_MATCH) {
          this.lastRule = i;
          return out;
        }
        continue;
      }
      if (!r.when!(ctx)) continue;
      this.lastRule = i;
      return r.then ? r.then(ctx) : (r.value as TResult);
    }
    this.lastRule = -1;
    if (!this.hasOtherwise) return NO_MATCH;
    return this.otherwise ? this.otherwise(ctx) : (this.otherwiseValue as TResult);
  }
}

/** Builder of a {@link FirstMatch}; get one from {@link FirstMatch.create}. Rule order is evaluation
 * order; names allow `before` / `after` / `replacing` / `remove` edits (see {@link OrderedBuilder}).
 * Mirrors C# `FirstMatch<TCtx, TResult>.Builder`. */
export class FirstMatchBuilder<TCtx, TResult> extends OrderedBuilder<Rule<TCtx, TResult>> {
  private hasOtherwise = false;
  private otherwiseFn: ((ctx: TCtx) => TResult) | null = null;
  private otherwiseConst: TResult | undefined = undefined;

  private readonly make: (
    names: readonly string[],
    rules: readonly Rule<TCtx, TResult>[],
    hasOtherwise: boolean,
    otherwise: ((ctx: TCtx) => TResult) | null,
    otherwiseValue: TResult | undefined,
  ) => FirstMatch<TCtx, TResult>;

  /** Internal: use {@link FirstMatch.create}. */
  constructor(
    make: (
      names: readonly string[],
      rules: readonly Rule<TCtx, TResult>[],
      hasOtherwise: boolean,
      otherwise: ((ctx: TCtx) => TResult) | null,
      otherwiseValue: TResult | undefined,
    ) => FirstMatch<TCtx, TResult>,
  ) {
    super();
    this.make = make;
  }

  /** Adds a rule: when `when` is true, `then` gives the result. C# `Builder.When(name, when, then)`. @returns this. */
  when(name: string, when: (ctx: TCtx) => boolean, then: (ctx: TCtx) => TResult): this {
    return this.put(name, { when, then, value: undefined, hasValue: false, tryRule: null });
  }

  /** Adds a rule: when `when` is true, the result is the constant `value`. C# `Builder.When(name, when, value)`. @returns this. */
  whenValue(name: string, when: (ctx: TCtx) => boolean, value: TResult): this {
    return this.put(name, { when, then: null, value, hasValue: true, tryRule: null });
  }

  /** Adds a rule that may decline after looking closer: return the result, or {@link NO_MATCH} to let
   * the next rule try. C# `Builder.Try` (a `TryRule` delegate). @returns this. */
  try(name: string, rule: (ctx: TCtx) => TResult | NoMatch): this {
    return this.put(name, { when: null, then: null, value: undefined, hasValue: false, tryRule: rule });
  }

  /** Sets the default computed when no rule matched (replaces any earlier default). C#
   * `Builder.Otherwise(Func)`. @returns this. */
  otherwise(fn: (ctx: TCtx) => TResult): this {
    this.hasOtherwise = true;
    this.otherwiseFn = fn;
    return this;
  }

  /** Sets a constant default (replaces any earlier default). C# `Builder.Otherwise(value)`. @returns this. */
  otherwiseValue(value: TResult): this {
    this.hasOtherwise = true;
    this.otherwiseFn = null;
    this.otherwiseConst = value;
    return this;
  }

  /** Freezes the rules into a {@link FirstMatch} (throws if a position is pending). The builder can
   * keep being edited and built again. C# `Builder.Build`. */
  build(): FirstMatch<TCtx, TResult> {
    const { names, entries } = this.snapshot();
    return this.make(names, entries, this.hasOtherwise, this.otherwiseFn, this.otherwiseConst);
  }
}

interface ActionRule<TCtx> {
  when: ((ctx: TCtx) => boolean) | null;
  then: ((ctx: TCtx) => void) | null;
  tryRule: ((ctx: TCtx) => boolean) | null;
}

/**
 * Runs an action: the first matching rule's, else the default's. Mirrors C# `FirstMatch<TCtx>` (the
 * single-type-parameter overload). Use it for `if / else if` ladders of side effects; for a ladder
 * that computes a value use {@link FirstMatch}.
 * @example
 * ```ts
 * const act = FirstMatchActions.create<Ctx>()
 *   .when('land', (c) => c.grounded && !c.wasGrounded, onLand)
 *   .try('wallJump', (c) => tryWallJump(c)) // true = acted
 *   .otherwise(airControl)
 *   .build();
 * act.run(ctx);
 * ```
 */
export class FirstMatchActions<TCtx> {
  /** Index of the rule that ran in the last `run` (-1: the default, or nothing). */
  lastRule = -1;

  private readonly ruleNames: readonly string[];
  private readonly rules: readonly ActionRule<TCtx>[];
  private readonly otherwise: ((ctx: TCtx) => void) | null;

  private constructor(ruleNames: readonly string[], rules: readonly ActionRule<TCtx>[], otherwise: ((ctx: TCtx) => void) | null) {
    this.ruleNames = ruleNames;
    this.rules = rules;
    this.otherwise = otherwise;
  }

  /** Starts a builder. Mirrors C# `FirstMatch<TCtx>.Create`. */
  static create<TCtx>(): FirstMatchActionsBuilder<TCtx> {
    return new FirstMatchActionsBuilder<TCtx>((n, r, o) => new FirstMatchActions(n, r, o));
  }

  /** Number of rules (the default not counted). C# `FirstMatch<TCtx>.Count`. */
  get count(): number {
    return this.rules.length;
  }

  /** Name of the rule at `index`. C# `FirstMatch<TCtx>.NameOf`. */
  nameOf(index: number): string {
    return this.ruleNames[index]!;
  }

  /** Index of the rule named `name`, or -1. C# `FirstMatch<TCtx>.IndexOf`. */
  indexOf(name: string): number {
    return this.ruleNames.indexOf(name);
  }

  /** Runs the first matching rule (or the default); false only when nothing ran. Sets
   * {@link lastRule}. C# `FirstMatch<TCtx>.Run`. */
  run(ctx: TCtx): boolean {
    const rules = this.rules;
    for (let i = 0; i < rules.length; i++) {
      const r = rules[i]!;
      if (r.tryRule) {
        if (r.tryRule(ctx)) {
          this.lastRule = i;
          return true;
        }
        continue;
      }
      if (!r.when!(ctx)) continue;
      r.then!(ctx);
      this.lastRule = i;
      return true;
    }
    this.lastRule = -1;
    if (!this.otherwise) return false;
    this.otherwise(ctx);
    return true;
  }
}

/** Builder of a {@link FirstMatchActions}; get one from {@link FirstMatchActions.create}. Mirrors C#
 * `FirstMatch<TCtx>.Builder`. */
export class FirstMatchActionsBuilder<TCtx> extends OrderedBuilder<ActionRule<TCtx>> {
  private otherwiseFn: ((ctx: TCtx) => void) | null = null;

  private readonly make: (names: readonly string[], rules: readonly ActionRule<TCtx>[], otherwise: ((ctx: TCtx) => void) | null) => FirstMatchActions<TCtx>;

  /** Internal: use {@link FirstMatchActions.create}. */
  constructor(make: (names: readonly string[], rules: readonly ActionRule<TCtx>[], otherwise: ((ctx: TCtx) => void) | null) => FirstMatchActions<TCtx>) {
    super();
    this.make = make;
  }

  /** Adds a rule: when `when` is true, run `then`. C# `Builder.When`. @returns this. */
  when(name: string, when: (ctx: TCtx) => boolean, then: (ctx: TCtx) => void): this {
    return this.put(name, { when, then, tryRule: null });
  }

  /** Adds a rule that acts and returns true, or declines (false) so the next rule tries. C# `Builder.Try`. @returns this. */
  try(name: string, rule: (ctx: TCtx) => boolean): this {
    return this.put(name, { when: null, then: null, tryRule: rule });
  }

  /** Sets the action run when no rule matched (replaces any earlier one). C# `Builder.Otherwise`. @returns this. */
  otherwise(fn: (ctx: TCtx) => void): this {
    this.otherwiseFn = fn;
    return this;
  }

  /** Freezes the rules into a {@link FirstMatchActions}. C# `Builder.Build`. */
  build(): FirstMatchActions<TCtx> {
    const { names, entries } = this.snapshot();
    return this.make(names, entries, this.otherwiseFn);
  }
}
