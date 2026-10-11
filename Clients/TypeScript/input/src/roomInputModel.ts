/**
 * Room input model — TypeScript twin of C# `Altruist.Gaming.Rooms.RoomInputModel<TInput>`.
 *
 * What a room's input is: a held-buttons bitmask plus held state (axes, aim, ...). Some buttons act
 * on their press (a rising edge of the bitmask, an "action"); the simulation applies the presses of
 * one step in a fixed order of stages, at most one press per stage. Declared once with the same
 * table as the server, the client can then compute presses, the canonical in-step order, whether
 * presses fit one step, and merges, with results identical to the server's (golden vectors in
 * `tests/golden/room-input-model.json` are produced by the C# implementation).
 */

/** Equality of two inputs. Must agree with the server's `EqualityComparer<TInput>.Default` for the
 * same values (a C# record struct compares every field). */
export type InputEquals<T> = (a: T, b: T) => boolean;

/** Number equality as C# `float.Equals`: `0 === -0`, and NaN equals NaN. */
function sameNumber(a: unknown, b: unknown): boolean {
  return a === b || (typeof a === 'number' && typeof b === 'number' && a !== a && b !== b);
}

/**
 * Field-wise equality of plain input objects (own enumerable keys compared with `===`, NaN equal to
 * NaN), or `===` for primitives. The default equality of a {@link RoomInputModel}: it matches a C#
 * `record struct` of the same fields. Supply your own through {@link RoomInputModelBuilder.equals}
 * when an input holds nested objects or arrays.
 * @example
 * ```ts
 * structuralEquals({ x: 0.5, buttons: 1 }, { x: 0.5, buttons: 1 }); // true
 * ```
 */
export function structuralEquals<T>(a: T, b: T): boolean {
  if (sameNumber(a, b)) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  if (ka.length !== kb.length) return false;
  for (const k of ka) {
    if (!Object.prototype.hasOwnProperty.call(b, k)) return false;
    if (!sameNumber((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k])) return false;
  }
  return true;
}

function trailingZeros(bit: number): number {
  return 31 - Math.clz32(bit & -bit);
}

/**
 * Fluent declaration of a {@link RoomInputModel}; start with {@link RoomInputModel.describe}, finish
 * with {@link RoomInputModelBuilder.build}. Mirrors C# `RoomInputModel<TInput>.Builder`; declare the
 * same buttons, actions and stages as the server's model so both sides agree on every press.
 */
export class RoomInputModelBuilder<T> {
  private _buttons: ((i: T) => number) | null = null;
  private _withButtons: ((i: T, b: number) => T) | null = null;
  private _starved: ((i: T) => T) | null = null;
  private _equals: InputEquals<T> = structuralEquals;
  private readonly _actions: [number, number][] = [];

  /** The held-buttons bitmask: how to read it and how to set it on a copy (never mutate `i`).
   * C# `Builder.Buttons(get, with)`. */
  buttons(get: (i: T) => number, withButtons: (i: T, buttons: number) => T): this {
    this._buttons = get;
    this._withButtons = withButtons;
    return this;
  }

  /** Buttons (bits of `mask`) that act on their press, applied at `stage` (0..30) of the simulation
   * step: lower stages first, at most one press per stage in a step; declaration order breaks ties
   * between bits of one stage. C# `Builder.Action(mask, stage)`. */
  action(mask: number, stage: number): this {
    this._actions.push([mask | 0, stage]);
    return this;
  }

  /** The input applied on a step with nothing queued, from the one applied last (default: the same
   * input). Use it to clear one-shot fields outside the bitmask. C# `Builder.WhenStarved`. */
  whenStarved(repeat: (last: T) => T): this {
    this._starved = repeat;
    return this;
  }

  /** TypeScript only: input equality (default {@link structuralEquals}), standing in for C#'s
   * `EqualityComparer<TInput>.Default`. Must agree with the server's equality. */
  equals(eq: InputEquals<T>): this {
    this._equals = eq;
    return this;
  }

  /** The declared model. Throws like C# `Builder.Build`: actions without `buttons(...)`, a stage
   * outside 0..30, or a bit declared in two actions. */
  build(): RoomInputModel<T> {
    if (this._actions.length > 0 && this._buttons === null) throw new Error('Action buttons need buttons(get, with) first.');
    return new RoomInputModel<T>(this._buttons, this._withButtons, this._starved, this._actions, this._equals);
  }
}

/**
 * What a room's input is: held-buttons bitmask, action buttons with their in-step stages, the
 * starved-step input and equality. Mirrors C# `RoomInputModel<TInput>` (same results for the same
 * declaration: verified against C# golden vectors).
 *
 * Use it on the client to:
 * - count and order presses exactly as the server does ({@link presses}, {@link appendInOrder});
 * - keep generated inputs canonical ({@link fitsOneStep}, {@link isCanonical}) — the
 *   {@link InputSequencer} does that from device events for you;
 * - run an {@link InputBuffer} locally (offline play, tests) with the server's catch-up rules.
 *
 * For inputs without action buttons use {@link RoomInputModel.opaque}. Pure and deterministic; all
 * masks are 32-bit ints.
 * @example
 * ```ts
 * type Pad = { x: number; buttons: number };
 * const JUMP = 1, FIRE = 2;
 * const model = RoomInputModel.describe<Pad>()
 *   .buttons((i) => i.buttons, (i, b) => ({ ...i, buttons: b }))
 *   .action(JUMP, 0)
 *   .action(FIRE, 1)
 *   .build();
 * model.presses({ x: 0, buttons: 0 }, { x: 0, buttons: JUMP | FIRE }); // JUMP | FIRE
 * ```
 */
export class RoomInputModel<T> {
  private readonly _buttons: ((i: T) => number) | null;
  private readonly _withButtons: ((i: T, b: number) => T) | null;
  private readonly _starved: ((i: T) => T) | null;
  private readonly _stage: Int32Array = new Int32Array(32).fill(-1);
  private readonly _order: number[];
  private readonly _eq: InputEquals<T>;
  /** Every button that acts on its press. C# `ActionMask`. */
  readonly actionMask: number;

  /** Starts a declaration. C# `RoomInputModel<TInput>.Describe()`. */
  static describe<T>(): RoomInputModelBuilder<T> {
    return new RoomInputModelBuilder<T>();
  }

  /** No declaration: inputs are opaque values, merged only when one equals the input applied before
   * it. C# `RoomInputModel<TInput>.Opaque` (TypeScript takes the equality, default
   * {@link structuralEquals}). */
  static opaque<T>(equals: InputEquals<T> = structuralEquals): RoomInputModel<T> {
    return new RoomInputModel<T>(null, null, null, [], equals);
  }

  /** @internal Use {@link RoomInputModel.describe} or {@link RoomInputModel.opaque}. */
  constructor(
    buttons: ((i: T) => number) | null,
    withButtons: ((i: T, b: number) => T) | null,
    starved: ((i: T) => T) | null,
    actions: readonly (readonly [number, number])[],
    equals: InputEquals<T>,
  ) {
    this._buttons = buttons;
    this._withButtons = withButtons;
    this._starved = starved;
    this._eq = equals;
    const order: { bit: number; stage: number; index: number }[] = [];
    let mask = 0;
    for (const [m, stage] of actions) {
      if (!Number.isInteger(stage) || stage < 0 || stage > 30) throw new RangeError('stages are 0..30');
      for (let b = 0; b < 32; b++) {
        if ((m & (1 << b)) === 0) continue;
        if (this._stage[b]! >= 0) throw new Error(`button bit ${b} is declared twice`);
        this._stage[b] = stage;
        order.push({ bit: 1 << b, stage, index: order.length });
        mask |= 1 << b;
      }
    }
    this.actionMask = mask;
    this._order = order.sort((a, b) => a.stage - b.stage || a.index - b.index).map((o) => o.bit);
  }

  /** A held-buttons bitmask is declared. C# `HasButtons`. */
  get hasButtons(): boolean {
    return this._buttons !== null;
  }

  /** Action bits in the order the simulation applies them within a step (stage, then declaration). */
  get order(): readonly number[] {
    return this._order;
  }

  /** The held-buttons bitmask of `i` (0 for an opaque model). */
  buttonsOf(i: T): number {
    return this._buttons === null ? 0 : this._buttons(i) | 0;
  }

  /** A copy of `i` holding `buttons` (requires {@link hasButtons}). */
  withButtons(i: T, buttons: number): T {
    if (this._withButtons === null) throw new Error('This model declares no buttons.');
    return this._withButtons(i, buttons);
  }

  /** The model's input equality (C# `EqualityComparer<TInput>.Default`). */
  equals(a: T, b: T): boolean {
    return this._eq(a, b);
  }

  /** Stage of a single button bit (-1: not an action, or not exactly one bit). C# `StageOf`. */
  stageOf(bit: number): number {
    bit |= 0;
    return bit === 0 || (bit & (bit - 1)) !== 0 ? -1 : this._stage[trailingZeros(bit)]!;
  }

  /** Presses (rising action bits) of `cur` after `prev`; 0 for an opaque model. C# `Presses`. */
  presses(prev: T, cur: T): number {
    return this._buttons === null ? 0 : this._buttons(cur) & ~this._buttons(prev) & this.actionMask;
  }

  /** Appends the bits of `presses` to `to` in the order the simulation applies them. C#
   * `AppendInOrder`. Allocation-free; for a fresh array use {@link inOrder}. */
  appendInOrder(presses: number, to: number[]): void {
    if (presses === 0) return;
    for (const b of this._order) if ((presses & b) !== 0) to.push(b);
  }

  /** The bits of `presses` in the order the simulation applies them (new array). */
  inOrder(presses: number): number[] {
    const out: number[] = [];
    this.appendInOrder(presses, out);
    return out;
  }

  /** The presses can share one step: at most one per stage. C# `FitsOneStep`. */
  fitsOneStep(presses: number): boolean {
    let seen = 0;
    for (const b of this._order) {
      if ((presses & b) === 0) continue;
      const s = 1 << this._stage[trailingZeros(b)]!;
      if ((seen & s) !== 0) return false;
      seen |= s;
    }
    return true;
  }

  /** Every press of `cur` after `prev` takes effect in its step, in its order (at most one per
   * stage): `fitsOneStep(presses(prev, cur))`. The property every sent input must have. */
  isCanonical(prev: T, cur: T): boolean {
    return this.fitsOneStep(this.presses(prev, cur));
  }

  /** The press sequence of a stream of inputs (each input against the previous, starting from
   * `start`), in step order then stage order. */
  pressSequence(stream: Iterable<T>, start: T): number[] {
    const out: number[] = [];
    let prev = start;
    for (const i of stream) {
      this.appendInOrder(this.presses(prev, i), out);
      prev = i;
    }
    return out;
  }

  /** The input of a starved step after `last` (see {@link RoomInputModelBuilder.whenStarved}). C#
   * `Starved`. */
  starved(last: T): T {
    return this._starved === null ? last : this._starved(last);
  }

  /**
   * One input for one step that does what `a` then `b` would do after `prev`, losing no press and
   * keeping their order; `undefined` when none exists (so `T` must not use `undefined` as a value).
   * Mirrors C# `TryMerge(prev, a, b, out merged)`: the merged input is `b` whenever one exists.
   */
  tryMerge(prev: T, a: T, b: T): T | undefined {
    if (this._eq(a, prev)) return b;
    if (this._buttons === null) return undefined;
    const pa = this.presses(prev, a);
    const pb = this.presses(a, b);
    const bPrev = this._buttons(prev);
    const bA = this._buttons(a);
    const bB = this._buttons(b);
    if ((bPrev & ~bA & bB & this.actionMask) !== 0) return undefined;
    if (pa === 0) return b;
    if (bB !== (bA | pb)) return undefined;
    if (!this._eq(this._withButtons!(a, 0), this._withButtons!(b, 0))) return undefined;
    if (pb !== 0 && (!this.fitsOneStep(pa | pb) || !this.allBefore(pa, pb))) return undefined;
    return b;
  }

  private allBefore(first: number, then: number): boolean {
    let maxFirst = -1;
    let minThen = Number.MAX_SAFE_INTEGER;
    for (let i = 0; i < this._order.length; i++) {
      const b = this._order[i]!;
      if ((first & b) !== 0) maxFirst = i;
      if ((then & b) !== 0 && minThen === Number.MAX_SAFE_INTEGER) minThen = i;
    }
    return maxFirst < minThen;
  }
}
