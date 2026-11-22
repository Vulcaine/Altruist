/** Math layer: scalars, angles, vectors, RNG, curves, geometry. No physics, no game concepts. */
export type { Vec2Like } from './vec2.ts';
export { vec2 } from './vec2.ts';
export * as Scalar from './scalar.ts';
export * as Angle from './angle.ts';
export * as PiecewiseLinear from './piecewiseLinear.ts';
export type { CurvePoint } from './piecewiseLinear.ts';
export { DeterministicRandom } from './deterministicRandom.ts';
export * as VectorMath2D from './vectorMath2D.ts';
export * as Direction2D from './direction2D.ts';
export * as Rotation2D from './rotation2D.ts';
export * as Geometry2D from './geometry2D.ts';
export * as Aabb2D from './aabb2D.ts';
export type { Aabb2DLike } from './aabb2D.ts';
export * as Polyline2D from './polyline2D.ts';
export { NormalFrame2D } from './normalFrame2D.ts';
export type { TangentSide2D } from './normalFrame2D.ts';
export type { BoxSide2D } from './geometry2D.ts';
export * as Distance2D from './distance2D.ts';
