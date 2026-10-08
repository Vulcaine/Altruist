/**
 * Flow layer. Per-tick input helpers — mirror of C# `ButtonEdges`, `InputLatch<T>` and `Stick`.
 */
/** Button edges of a held-buttons bitmask: `update(buttons)` returns `buttons & ~previous`. */
export class ButtonEdges {
  /** Buttons held after the last update (store with the entity). */
  held: number;
  /** Buttons pressed in the last update. */
  pressed = 0;
  /** Buttons released in the last update. */
  released = 0;

  constructor(held = 0) {
    this.held = held;
  }

  update(buttons: number): number {
    const prev = this.held;
    this.pressed = buttons & ~prev;
    this.released = prev & ~buttons;
    this.held = buttons;
    return this.pressed;
  }

  wasPressed(mask: number): boolean {
    return (this.pressed & mask) !== 0;
  }

  isHeld(mask: number): boolean {
    return (this.held & mask) !== 0;
  }

  wasReleased(mask: number): boolean {
    return (this.released & mask) !== 0;
  }
}

/** The arrived input, or a repeat of the last one when none arrived; stored as the new last. */
export class InputLatch<T> {
  last: T;

  constructor(neutral: T) {
    this.last = neutral;
  }

  /** `input ?? last` (undefined = nothing arrived); stored as last. */
  resolve(input: T | undefined): T {
    const resolved = input === undefined ? this.last : input;
    this.last = resolved;
    return resolved;
  }

  /** The input for `key` in this tick's map, else the last one. */
  resolveFrom<K>(inputs: ReadonlyMap<K, T>, key: K): T {
    return this.resolve(inputs.get(key));
  }
}
