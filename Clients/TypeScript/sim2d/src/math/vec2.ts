/**
 * A 2D vector as a plain `{ x, y }` object. Every helper accepts anything with `x` and `y`
 * (planck's `Vec2`, Pixi points, literals) and returns a new plain object; inputs are never
 * mutated. Mirrors `System.Numerics.Vector2` in the C# API.
 */
/**
 * Structural 2D vector `{ x, y }` (world units, +Y up). The TS stand-in for C#
 * `System.Numerics.Vector2`; anything with numeric `x`/`y` fits (planck `Vec2`, Pixi points,
 * literals). Helpers in this package read it and return new objects, never mutating inputs.
 */
export interface Vec2Like {
  /** Horizontal component (+X right). */
  x: number;
  /** Vertical component (+Y up). */
  y: number;
}

/**
 * Builds a plain `{ x, y }` literal. Equivalent to C# `new Vector2(x, y)`.
 * @param x - Horizontal component.
 * @param y - Vertical component.
 * @returns A new plain object (not a planck `Vec2`).
 */
export function vec2(x: number, y: number): Vec2Like {
  return { x, y };
}
