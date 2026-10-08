/**
 * Flow layer. Sets of small integer ids as the bits of a 32-bit int — mirror of C# `IdMask32`
 * (bit = `1 << (id & 31)`; the same 32-bit expressions, so masks hold the same integers as C#).
 */

/** `1 << (id & 31)`. */
export function bit(id: number): number {
  return 1 << (id & 31);
}

/** True when `id`'s bit is set. */
export function has(mask: number, id: number): boolean {
  return (mask & bit(id)) !== 0;
}

/** The mask with `id`'s bit set. */
export function withId(mask: number, id: number): number {
  return mask | bit(id);
}

/** The mask with `id`'s bit cleared. */
export function withoutId(mask: number, id: number): number {
  return mask & ~bit(id);
}

/** Captures a set at an event: the bits of every item (list order) for which `include` is true. */
export function collect<T>(items: readonly T[], include: (item: T) => boolean, idOf: (item: T) => number): number {
  let mask = 0;
  for (let i = 0; i < items.length; i++) {
    const item = items[i]!;
    if (include(item)) mask |= bit(idOf(item));
  }
  return mask;
}

