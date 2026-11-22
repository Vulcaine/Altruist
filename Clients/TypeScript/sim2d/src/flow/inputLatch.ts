/**
 * Flow layer. Per-tick input helpers — mirror of C# `ButtonEdges`, `InputLatch<T>` and `Stick`.
 */
/** Button edges of a held-buttons bitmask: `update(buttons)` returns `buttons & ~previous`. Mirrors the
 * C# struct `ButtonEdges`.
 * @example
 * ```ts
 * edges.update(input.buttons);
 * if (edges.wasPressed(JUMP)) jump();
 * ```
 */
export class ButtonEdges {
  /** Buttons held after the last update (store with the entity). C# `ButtonEdges.Held`. */
  held: number;
  /** Buttons pressed in the last update (`buttons & ~previous`). C# `ButtonEdges.Pressed`. */
  pressed = 0;
  /** Buttons released in the last update (`previous & ~buttons`). C# `ButtonEdges.Released`. */
  released = 0;

  /** Restores from a stored `held` mask (no edges yet). C# `new ButtonEdges(held)`. */
  constructor(held = 0) {
    this.held = held;
  }

  /** Feeds this tick's held mask; computes pressed / released and returns the pressed mask. C#
   * `ButtonEdges.Update`. */
  update(buttons: number): number {
    const prev = this.held;
    this.pressed = buttons & ~prev;
    this.released = prev & ~buttons;
    this.held = buttons;
    return this.pressed;
  }

  /** True when any button of `mask` was pressed in the last update. C# `ButtonEdges.WasPressed`. */
  wasPressed(mask: number): boolean {
    return (this.pressed & mask) !== 0;
  }

  /** True when any button of `mask` is held. C# `ButtonEdges.IsHeld`. */
  isHeld(mask: number): boolean {
    return (this.held & mask) !== 0;
  }

  /** True when any button of `mask` was released in the last update. C# `ButtonEdges.WasReleased`. */
  wasReleased(mask: number): boolean {
    return (this.released & mask) !== 0;
  }
}

/** The arrived input, or a repeat of the last one when none arrived (packet loss / late input);
 * stored as the new last. Mirrors C# `InputLatch<T>`. `undefined` means "nothing arrived", so `T`
 * should not use `undefined` as a real value. */
export class InputLatch<T> {
  /** The last resolved input (writable for restore). C# `InputLatch.Last`. */
  last: T;

  /** Starts with `neutral` as the last input. C# `new InputLatch(neutral)`. */
  constructor(neutral: T) {
    this.last = neutral;
  }

  /** `input === undefined ? last : input` (null counts as arrived); stored as last. C#
   * `InputLatch.Resolve(arrived, input)`. */
  resolve(input: T | undefined): T {
    const resolved = input === undefined ? this.last : input;
    this.last = resolved;
    return resolved;
  }

  /** The input for `key` in this tick's map, else the last one. C# `InputLatch.Resolve(inputs, key)`. */
  resolveFrom<K>(inputs: ReadonlyMap<K, T>, key: K): T {
    return this.resolve(inputs.get(key));
  }
}
