/**
 * Math layer. Vector algebra on `{ x, y }` — mirror of C# `Altruist.TwoD.Numerics.VectorMath2D`.
 * Same expressions, component by component, same order; returns new objects.
 * Lengths are `Math.sqrt(x * x + y * y)`, the C# expression — not `Math.hypot`, which rounds
 * differently (it can differ in the last bit), so code using `Math.hypot` keeps its own bits only
 * while it keeps calling it.
 */
import { fromPolarDegrees } from './direction2D.ts';
import { approach } from './scalar.ts';
import type { Vec2Like } from './vec2.ts';

/** Euclidean length of `(x, y)`: `Math.sqrt(x * x + y * y)` (not `Math.hypot`). Mirrors C#
 * `VectorMath2D.Length(float, float)`. */
export function lengthXY(x: number, y: number): number {
  return Math.sqrt(x * x + y * y);
}

/** Euclidean length `Math.sqrt(v.x * v.x + v.y * v.y)` (not `Math.hypot`). Mirrors C#
 * `VectorMath2D.Length(Vector2)`. */
export function length(v: Vec2Like): number {
  return Math.sqrt(v.x * v.x + v.y * v.y);
}

/** Unit vector, or `(0, 0)` for an exactly-zero vector: `l = length(v) || 1; (v.x / l, v.y / l)`
 * (no epsilon: tiny vectors still normalize). Mirrors C# `VectorMath2D.NormalizeOrZero(Vector2)`.
 * Use {@link normalizeOr} when a zero input needs a meaningful default direction, and
 * {@link Direction2D.between} when near-coincident points (closer than 1e-6) should count as zero. */
export function normalizeOrZero(v: Vec2Like): Vec2Like {
  const l = Math.sqrt(v.x * v.x + v.y * v.y) || 1;
  return { x: v.x / l, y: v.y / l };
}

/** {@link normalizeOrZero} plus the length (0 for a zero vector), computed once. Mirrors C#
 * `VectorMath2D.NormalizeOrZero(Vector2, out float length)`. */
export function normalizeOrZeroWithLength(v: Vec2Like): { direction: Vec2Like; length: number } {
  const len = Math.sqrt(v.x * v.x + v.y * v.y);
  const l = len || 1;
  return { direction: { x: v.x / l, y: v.y / l }, length: len };
}

/** Unit vector, or `fallback` (returned as given, not normalized or copied) for an exactly-zero
 * vector: `l = length(v); l === 0 ? fallback : (v.x / l, v.y / l)`. Mirrors C#
 * `VectorMath2D.NormalizeOr`. Compare {@link normalizeOrZero}, which returns `(0, 0)` instead. */
export function normalizeOr(v: Vec2Like, fallback: Vec2Like): Vec2Like {
  const l = Math.sqrt(v.x * v.x + v.y * v.y);
  return l === 0 ? fallback : { x: v.x / l, y: v.y / l };
}

/** 2D cross product (z of the 3D cross): `a.x * b.y - a.y * b.x`; positive when `b` is
 * counter-clockwise of `a`. Mirrors C# `VectorMath2D.Cross(Vector2, Vector2)`. */
export function cross(a: Vec2Like, b: Vec2Like): number {
  return a.x * b.y - a.y * b.x;
}

/** Angular velocity × arm, ω × r: `(-w * r.y, w * r.x)` (velocity of a point at `r` from the center
 * of a body spinning at `w` rad/s). Mirrors C# `VectorMath2D.Cross(float, Vector2)`. */
export function crossScalar(w: number, r: Vec2Like): Vec2Like {
  return { x: -w * r.y, y: w * r.x };
}

/** Part of `v` along the unit `axis`: `axis * (v·axis)`. Mirrors C# `VectorMath2D.Project`.
 * Complement of {@link reject}. */
export function project(v: Vec2Like, axis: Vec2Like): Vec2Like {
  const d = v.x * axis.x + v.y * axis.y;
  return { x: axis.x * d, y: axis.y * d };
}

/** Part of `v` perpendicular to the unit `normal` (the tangential part): `v - normal * (v·normal)`.
 * Mirrors C# `VectorMath2D.Reject`. Complement of {@link project}. */
export function reject(v: Vec2Like, normal: Vec2Like): Vec2Like {
  const d = v.x * normal.x + v.y * normal.y;
  return { x: v.x - normal.x * d, y: v.y - normal.y * d };
}

/** `v + direction * amount`: `(v.x + direction.x * amount, v.y + direction.y * amount)`. Pure;
 * the body-mutating form is {@link BodyMotion2D.pushAlong}. Mirrors C# `VectorMath2D.AddAlong`. */
export function addAlong(v: Vec2Like, direction: Vec2Like, amount: number): Vec2Like {
  return { x: v.x + direction.x * amount, y: v.y + direction.y * amount };
}

/** Sets the component along the unit `direction` to `value`, keeping the perpendicular part:
 * `c = v.x * d.x + v.y * d.y; addAlong(v, d, value - c)`. Pure; the body form is
 * {@link BodyMotion2D.setSpeedAlong}. Mirrors C# `VectorMath2D.WithComponentAlong`. */
export function withComponentAlong(v: Vec2Like, direction: Vec2Like, value: number): Vec2Like {
  const c = v.x * direction.x + v.y * direction.y;
  return addAlong(v, direction, value - c);
}

/** Moves the component along `direction` toward `target` by at most `maxDelta`:
 * `c = v·d; addAlong(v, d, approach(c, target, maxDelta) - c)`. Use for acceleration-limited
 * steering along one axis. Mirrors C# `VectorMath2D.ApproachComponentAlong`. */
export function approachComponentAlong(v: Vec2Like, direction: Vec2Like, target: number, maxDelta: number): Vec2Like {
  const c = v.x * direction.x + v.y * direction.y;
  const next = approach(c, target, maxDelta);
  return addAlong(v, direction, next - c);
}

/** All of v's length onto the unit axis, on v's side: `s = v·axis >= 0 ? |v| : -|v|; axis * s`
 * (keeps speed, changes direction; contrast {@link project}, which loses the off-axis part).
 * Mirrors C# `VectorMath2D.RedirectAlong`. */
export function redirectAlong(v: Vec2Like, axis: Vec2Like): Vec2Like {
  const speed = Math.sqrt(v.x * v.x + v.y * v.y);
  const s = v.x * axis.x + v.y * axis.y >= 0 ? speed : -speed;
  return { x: axis.x * s, y: axis.y * s };
}

/** Caps the length at `maxLength`: `s = |v|; s > maxLength ? (v.x / s * maxLength, v.y / s * maxLength) : v`
 * (always returns a copy). Mirrors C# `VectorMath2D.ClampLength`. */
export function clampLength(v: Vec2Like, maxLength: number): Vec2Like {
  const s = Math.sqrt(v.x * v.x + v.y * v.y);
  return s > maxLength ? { x: (v.x / s) * maxLength, y: (v.y / s) * maxLength } : { x: v.x, y: v.y };
}

/** Shortens `v` by `amount` given its already-computed `length` (no sqrt; caller guarantees
 * `length > 0`; overshoots past zero if `amount > length`):
 * `(v.x - v.x / length * amount, v.y - v.y / length * amount)`. Mirrors C# `VectorMath2D.ShortenBy`. */
export function shortenBy(v: Vec2Like, length: number, amount: number): Vec2Like {
  return { x: v.x - (v.x / length) * amount, y: v.y - (v.y / length) * amount };
}

/** `v` rotated counter-clockwise by `degrees`:
 * `{ x: c, y: s } = Direction2D.fromPolarDegrees(degrees); (v.x * c - v.y * s, v.x * s + v.y * c)`.
 * The radians are `degrees * (π / 180)`; the C# twin computes `(degrees * π) / 180` (as float32
 * code typically writes it). Mirrors C# `VectorMath2D.RotateDegrees`. For radians see
 * {@link Rotation2D.rotate}. */
export function rotateDegrees(v: Vec2Like, degrees: number): Vec2Like {
  const { x: c, y: s } = fromPolarDegrees(degrees);
  return { x: v.x * c - v.y * s, y: v.x * s + v.y * c };
}

/** Component-wise linear interpolation `a + (b - a) * t` (unclamped). Mirrors C# `VectorMath2D.Lerp`. */
export function lerp(a: Vec2Like, b: Vec2Like, t: number): Vec2Like {
  return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
}
