/**
 * Flow layer. An ordered list of number modifiers — mirror of C# `ModifierStack<TCtx>`. Each entry
 * is one operation applied as written, in list order: `set` (value = x), `add` (value + x), `mul`
 * (value * x), `min` (`Math.min(value, x)`), `max` (`Math.max(value, x)`), `map` (any function).
 * An operand function runs only when its entry applies. `when` gates an entry; `else()` makes the
 * next entry the else-branch of the previous one (considered only when no entry of its chain applied).
 */
import { OrderedBuilder } from './orderedBuilder.ts';

/** The operation of one modifier entry. Mirrors the C# enum `ModifierOp` (`Set`, `Add`, `Mul`, `Min`,
 * `Max`, `Map`). */
export type ModifierOp = 'set' | 'add' | 'mul' | 'min' | 'max' | 'map';

type Operand<TCtx> = number | ((ctx: TCtx) => number);

interface Entry<TCtx> {
  op: ModifierOp;
  constant: number;
  operand: ((ctx: TCtx) => number) | null;
  map: ((ctx: TCtx, value: number) => number) | null;
  when: ((ctx: TCtx) => boolean) | null;
  isElse: boolean;
}

/**
 * An ordered list of number modifiers applied to a base value (speed caps, gravity scales, damage
 * multipliers...). Mirrors C# `ModifierStack<TCtx>`. Replaces chains of `if (x) v *= k; else if ...`
 * so each modifier is named and can be inserted / replaced by name. TS uses doubles; the C# twin is
 * float32 with the same operation order.
 * @example
 * ```ts
 * const gravityScale = ModifierStack.create<Body>()
 *   .set('base', 1)
 *   .mul('gliding', 0.3, (b) => b.gliding)
 *   .else().mul('fastFall', 2, (b) => b.downHeld) // only when 'gliding' did not apply
 *   .min('cap', (b) => b.maxScale)
 *   .build();
 * const g = gravityScale.apply(1, body);
 * ```
 */
export class ModifierStack<TCtx> {
  private readonly entryNames: readonly string[];
  private readonly entries: readonly Entry<TCtx>[];

  private constructor(names: readonly string[], entries: readonly Entry<TCtx>[]) {
    this.entryNames = names;
    this.entries = entries;
  }

  /** Starts a builder. Mirrors C# `ModifierStack<TCtx>.Create`. */
  static create<TCtx>(): ModifierStackBuilder<TCtx> {
    return new ModifierStackBuilder<TCtx>((n, e) => new ModifierStack(n, e));
  }

  /** Number of entries. C# `ModifierStack.Count`. */
  get count(): number {
    return this.entries.length;
  }

  /** Name of the entry at `index`. C# `ModifierStack.NameOf`. */
  nameOf(index: number): string {
    return this.entryNames[index]!;
  }

  /** Operation of the entry at `index`. C# `ModifierStack.OpOf`. */
  opOf(index: number): ModifierOp {
    return this.entries[index]!.op;
  }

  /** Applies every modifier in order to `value` and returns the result. An entry is skipped when its
   * `when` is false, or when it is an else-branch and an entry of its chain already applied. Operand
   * functions run only for applied entries. Pure apart from what the callbacks do. C# `ModifierStack.Apply`. */
  apply(value: number, ctx: TCtx): number {
    const entries = this.entries;
    let chainTaken = false;
    for (let i = 0; i < entries.length; i++) {
      const e = entries[i]!;
      if (e.isElse && chainTaken) continue;
      if (e.when && !e.when(ctx)) {
        if (!e.isElse) chainTaken = false;
        continue;
      }
      chainTaken = true;
      switch (e.op) {
        case 'set':
          value = e.operand ? e.operand(ctx) : e.constant;
          break;
        case 'add':
          value = value + (e.operand ? e.operand(ctx) : e.constant);
          break;
        case 'mul':
          value = value * (e.operand ? e.operand(ctx) : e.constant);
          break;
        case 'min':
          value = Math.min(value, e.operand ? e.operand(ctx) : e.constant);
          break;
        case 'max':
          value = Math.max(value, e.operand ? e.operand(ctx) : e.constant);
          break;
        default:
          value = e.map!(ctx, value);
          break;
      }
    }
    return value;
  }
}

/** Builder of a {@link ModifierStack}; get one from {@link ModifierStack.create}. Each op takes a
 * constant or a `(ctx) => number` operand and an optional `when` gate. Mirrors C#
 * `ModifierStack<TCtx>.Builder` (whose constant / function overloads become one union parameter). */
export class ModifierStackBuilder<TCtx> extends OrderedBuilder<Entry<TCtx>> {
  private readonly make: (names: readonly string[], entries: readonly Entry<TCtx>[]) => ModifierStack<TCtx>;
  private pendingElse = false;

  /** Internal: use {@link ModifierStack.create}. */
  constructor(make: (names: readonly string[], entries: readonly Entry<TCtx>[]) => ModifierStack<TCtx>) {
    super();
    this.make = make;
  }

  /** Makes the next added modifier the else-branch of the previous one: it is considered only when no
   * entry of the chain applied (chain several for else-if). Throws when there is no previous modifier.
   * C# `Builder.Else`. @returns this. */
  else(): this {
    if (this.names.length === 0) throw new Error('else() needs a previous modifier.');
    this.pendingElse = true;
    return this;
  }

  /** Adds `value = x`. C# `Builder.Set`. @returns this. */
  set(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'set', value, when);
  }

  /** Adds `value = value + x`. C# `Builder.Add`. @returns this. */
  add(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'add', value, when);
  }

  /** Adds `value = value * x`. C# `Builder.Mul`. @returns this. */
  mul(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'mul', value, when);
  }

  /** Adds `value = Math.min(value, x)` (an upper cap). C# `Builder.Min`. @returns this. */
  min(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'min', value, when);
  }

  /** Adds `value = Math.max(value, x)` (a lower floor). C# `Builder.Max`. @returns this. */
  max(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'max', value, when);
  }

  /** Adds `value = map(ctx, value)` (any function). C# `Builder.Map`. @returns this. */
  map(name: string, map: (ctx: TCtx, value: number) => number, when?: (ctx: TCtx) => boolean): this {
    const isElse = this.pendingElse;
    this.pendingElse = false;
    return this.put(name, { op: 'map', constant: 0, operand: null, map, when: when ?? null, isElse });
  }

  /** Freezes the entries into a {@link ModifierStack}; throws if `else()` or a position is pending.
   * C# `Builder.Build`. */
  build(): ModifierStack<TCtx> {
    if (this.pendingElse) throw new Error('else() was set but no modifier followed it.');
    const { names, entries } = this.snapshot();
    return this.make(names, entries);
  }

  private push(name: string, op: ModifierOp, value: Operand<TCtx>, when: ((ctx: TCtx) => boolean) | undefined): this {
    const isElse = this.pendingElse;
    this.pendingElse = false;
    const fn = typeof value === 'function' ? value : null;
    return this.put(name, { op, constant: fn ? 0 : (value as number), operand: fn, map: null, when: when ?? null, isElse });
  }
}
