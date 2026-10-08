/**
 * Flow layer. Ordered rule selectors — mirror of C# `FirstMatch<TCtx, TResult>` (here `FirstMatch`)
 * and `FirstMatch<TCtx>` (here `FirstMatchActions`): the replacement for `if / else if` ladders.
 * Rules run in build order; the first match decides and later rules are not evaluated. A `try`
 * rule may decline after looking closer (returns `NO_MATCH`; C#: `TryRule` returning false).
 * Every predicate up to the matching rule runs exactly once, in order.
 */
import { OrderedBuilder } from './orderedBuilder.ts';

/** Returned by a `try` rule to decline (the next rule is tried). */
export const NO_MATCH: unique symbol = Symbol('NO_MATCH');
export type NoMatch = typeof NO_MATCH;

interface Rule<TCtx, TResult> {
  when: ((ctx: TCtx) => boolean) | null;
  then: ((ctx: TCtx) => TResult) | null;
  value: TResult | undefined;
  hasValue: boolean;
  tryRule: ((ctx: TCtx) => TResult | NoMatch) | null;
}

/** Picks a result: the first matching rule's, else the default's. */
export class FirstMatch<TCtx, TResult> {
  /** Index of the rule that answered the last evaluation (-1: the default, or nothing matched). */
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

  static create<TCtx, TResult>(): FirstMatchBuilder<TCtx, TResult> {
    return new FirstMatchBuilder<TCtx, TResult>((n, r, h, o, v) => new FirstMatch(n, r, h, o, v));
  }

  /** Number of rules (the default not counted). */
  get count(): number {
    return this.rules.length;
  }

  /** True when a default answers if no rule matched. */
  get hasDefault(): boolean {
    return this.hasOtherwise;
  }

  nameOf(index: number): string {
    return this.ruleNames[index]!;
  }

  indexOf(name: string): number {
    return this.ruleNames.indexOf(name);
  }

  /** The first matching rule's result, else the default's; throws when nothing matched and there is no default. */
  evaluate(ctx: TCtx): TResult {
    const r = this.tryEvaluate(ctx);
    if (r === NO_MATCH) throw new Error('No rule matched and there is no default.');
    return r;
  }

  /** The first matching rule's result, else the default's, else `NO_MATCH`. */
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

  /** Use `FirstMatch.create()`. */
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

  /** When `when` is true, `then` gives the result. */
  when(name: string, when: (ctx: TCtx) => boolean, then: (ctx: TCtx) => TResult): this {
    return this.put(name, { when, then, value: undefined, hasValue: false, tryRule: null });
  }

  /** When `when` is true, the result is `value`. */
  whenValue(name: string, when: (ctx: TCtx) => boolean, value: TResult): this {
    return this.put(name, { when, then: null, value, hasValue: true, tryRule: null });
  }

  /** A rule that may decline (returns `NO_MATCH`) after looking closer. */
  try(name: string, rule: (ctx: TCtx) => TResult | NoMatch): this {
    return this.put(name, { when: null, then: null, value: undefined, hasValue: false, tryRule: rule });
  }

  /** The result when no rule matched. */
  otherwise(fn: (ctx: TCtx) => TResult): this {
    this.hasOtherwise = true;
    this.otherwiseFn = fn;
    return this;
  }

  /** The constant result when no rule matched. */
  otherwiseValue(value: TResult): this {
    this.hasOtherwise = true;
    this.otherwiseFn = null;
    this.otherwiseConst = value;
    return this;
  }

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

/** Runs an action: the first matching rule's, else the default's (C# `FirstMatch<TCtx>`). */
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

  static create<TCtx>(): FirstMatchActionsBuilder<TCtx> {
    return new FirstMatchActionsBuilder<TCtx>((n, r, o) => new FirstMatchActions(n, r, o));
  }

  get count(): number {
    return this.rules.length;
  }

  nameOf(index: number): string {
    return this.ruleNames[index]!;
  }

  indexOf(name: string): number {
    return this.ruleNames.indexOf(name);
  }

  /** Runs the first matching rule (or the default); false only when nothing ran. */
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

export class FirstMatchActionsBuilder<TCtx> extends OrderedBuilder<ActionRule<TCtx>> {
  private otherwiseFn: ((ctx: TCtx) => void) | null = null;

  private readonly make: (names: readonly string[], rules: readonly ActionRule<TCtx>[], otherwise: ((ctx: TCtx) => void) | null) => FirstMatchActions<TCtx>;

  /** Use `FirstMatchActions.create()`. */
  constructor(make: (names: readonly string[], rules: readonly ActionRule<TCtx>[], otherwise: ((ctx: TCtx) => void) | null) => FirstMatchActions<TCtx>) {
    super();
    this.make = make;
  }

  /** When `when` is true, run `then`. */
  when(name: string, when: (ctx: TCtx) => boolean, then: (ctx: TCtx) => void): this {
    return this.put(name, { when, then, tryRule: null });
  }

  /** A rule that acts and returns true, or declines (false) so the next rule tries. */
  try(name: string, rule: (ctx: TCtx) => boolean): this {
    return this.put(name, { when: null, then: null, tryRule: rule });
  }

  /** The action when no rule matched. */
  otherwise(fn: (ctx: TCtx) => void): this {
    this.otherwiseFn = fn;
    return this;
  }

  build(): FirstMatchActions<TCtx> {
    const { names, entries } = this.snapshot();
    return this.make(names, entries, this.otherwiseFn);
  }
}
