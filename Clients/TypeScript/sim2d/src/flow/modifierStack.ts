/**
 * Flow layer. An ordered list of number modifiers — mirror of C# `ModifierStack<TCtx>`. Each entry
 * is one operation applied as written, in list order: `set` (value = x), `add` (value + x), `mul`
 * (value * x), `min` (`Math.min(value, x)`), `max` (`Math.max(value, x)`), `map` (any function).
 * An operand function runs only when its entry applies. `when` gates an entry; `else()` makes the
 * next entry the else-branch of the previous one (considered only when no entry of its chain applied).
 */
import { OrderedBuilder } from './orderedBuilder.ts';

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

export class ModifierStack<TCtx> {
  private readonly entryNames: readonly string[];
  private readonly entries: readonly Entry<TCtx>[];

  private constructor(names: readonly string[], entries: readonly Entry<TCtx>[]) {
    this.entryNames = names;
    this.entries = entries;
  }

  static create<TCtx>(): ModifierStackBuilder<TCtx> {
    return new ModifierStackBuilder<TCtx>((n, e) => new ModifierStack(n, e));
  }

  get count(): number {
    return this.entries.length;
  }

  nameOf(index: number): string {
    return this.entryNames[index]!;
  }

  opOf(index: number): ModifierOp {
    return this.entries[index]!.op;
  }

  /** Applies every modifier in order to `value`. */
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

export class ModifierStackBuilder<TCtx> extends OrderedBuilder<Entry<TCtx>> {
  private readonly make: (names: readonly string[], entries: readonly Entry<TCtx>[]) => ModifierStack<TCtx>;
  private pendingElse = false;

  /** Use `ModifierStack.create()`. */
  constructor(make: (names: readonly string[], entries: readonly Entry<TCtx>[]) => ModifierStack<TCtx>) {
    super();
    this.make = make;
  }

  /** The next modifier is the else-branch of the previous one. */
  else(): this {
    if (this.names.length === 0) throw new Error('else() needs a previous modifier.');
    this.pendingElse = true;
    return this;
  }

  /** `value = x` */
  set(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'set', value, when);
  }

  /** `value = value + x` */
  add(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'add', value, when);
  }

  /** `value = value * x` */
  mul(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'mul', value, when);
  }

  /** `value = Math.min(value, x)` */
  min(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'min', value, when);
  }

  /** `value = Math.max(value, x)` */
  max(name: string, value: Operand<TCtx>, when?: (ctx: TCtx) => boolean): this {
    return this.push(name, 'max', value, when);
  }

  /** `value = map(ctx, value)` */
  map(name: string, map: (ctx: TCtx, value: number) => number, when?: (ctx: TCtx) => boolean): this {
    const isElse = this.pendingElse;
    this.pendingElse = false;
    return this.put(name, { op: 'map', constant: 0, operand: null, map, when: when ?? null, isElse });
  }

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
