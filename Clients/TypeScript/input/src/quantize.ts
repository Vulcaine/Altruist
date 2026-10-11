/**
 * Quantization helpers: make a client's input identical to what the server decodes from the wire,
 * so prediction steps the exact same input. Built on `@altruist/sim2d` `Scalar.quantize`.
 */

import { quantize } from '../../sim2d/src/math/scalar.ts';

export { quantize };

/**
 * A function that quantizes the named numeric fields of an input (copy; other fields kept) with
 * `Scalar.quantize(v, steps)` (`Math.round(clamp(v, -1, 1) * steps) / steps`; 127 = a signed byte).
 * Apply it to every input before predicting and sending, with the steps the wire format uses. Note
 * the C# twin rounds exact ties to even: the server decodes the byte, so only the client's rounding
 * matters and the byte round-trip is exact.
 * @example
 * ```ts
 * const q = quantizer<Pad>(['x', 'y']);
 * q({ x: 0.123456, y: 2, buttons: 1 }); // { x: 16/127, y: 1, buttons: 1 }
 * ```
 */
export function quantizer<T extends object>(fields: readonly (keyof T)[], steps = 127): (input: T) => T {
  return (input) => {
    const out = { ...input };
    for (const f of fields) (out as Record<keyof T, unknown>)[f] = quantize(input[f] as number, steps);
    return out;
  };
}
