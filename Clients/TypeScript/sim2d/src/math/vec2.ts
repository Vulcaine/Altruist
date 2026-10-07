/**
 * A 2D vector as a plain `{ x, y }` object. Every helper accepts anything with `x` and `y`
 * (planck's `Vec2`, Pixi points, literals) and returns a new plain object; inputs are never
 * mutated. Mirrors `System.Numerics.Vector2` in the C# API.
 */
export interface Vec2Like {
  x: number;
  y: number;
}

export function vec2(x: number, y: number): Vec2Like {
  return { x, y };
}
