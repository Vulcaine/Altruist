/**
 * Multi-bit value fields packed into a held-buttons word (a direction index, a weapon slot, an
 * analog bucket). Client-side helper: C# reads the same bits with plain shifts.
 */

/**
 * A `width`-bit unsigned value stored at bit `shift` of a 32-bit button word. Use it for held state
 * that is a number rather than independent buttons (an 8-way direction index in 3 bits, say), so
 * the bits stay out of the model's action mask and are treated as held state: the
 * {@link RoomInputModel} never sees a press in them, the {@link InputSequencer} applies a value
 * change as a whole together with the change it arrived in, and merges compare it as held state.
 *
 * Declare actions on other bits only (never on a field's bits). Works for any bits 0..31 (bit 31
 * reads back as a negative 32-bit int in the full word, which is fine for masks).
 * @example
 * ```ts
 * const DIR = new ButtonField(6, 3);        // bits 6..8, values 0..7
 * let buttons = DIR.write(0, 5);           // 5 << 6
 * DIR.read(buttons);                       // 5
 * DIR.mask;                                // 0b111 << 6 = 448
 * ```
 */
export class ButtonField {
  /** First bit of the field. */
  readonly shift: number;
  /** Number of bits (1..32 - shift). */
  readonly width: number;
  /** The field's bits in the button word. */
  readonly mask: number;
  /** Largest storable value (`2^width - 1`). */
  readonly max: number;

  /** A field of `width` bits starting at bit `shift`. Throws when it does not fit 32 bits. */
  constructor(shift: number, width: number) {
    if (!Number.isInteger(shift) || !Number.isInteger(width) || shift < 0 || width < 1 || shift + width > 32)
      throw new RangeError('a button field must fit bits 0..31');
    this.shift = shift;
    this.width = width;
    this.max = 2 ** width - 1;
    this.mask = (this.max << shift) | 0;
  }

  /** The field's value in `buttons` (0..max). */
  read(buttons: number): number {
    return ((buttons & this.mask) >>> this.shift) >>> 0;
  }

  /** `buttons` with the field set to `value` (masked to the field width; other bits kept). */
  write(buttons: number, value: number): number {
    return ((buttons & ~this.mask) | ((value << this.shift) & this.mask)) | 0;
  }
}
