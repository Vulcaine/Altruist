/**
 * @altruist/sim2d — TypeScript twin of Altruist's 2D math, physics and gameplay helpers.
 * Three layers, each built only on the one below:
 *   math     (`@altruist/sim2d/math`)     scalars, angles, vectors, RNG, curves, geometry
 *   physics  (`@altruist/sim2d/physics`)  velocity / angular ops, gravity, contacts, rays, ballistics
 *   gameplay (`@altruist/sim2d/gameplay`) designer-level verbs: jumpOff, bounceOff, alignToSurface...
 */
export * from './math/index.ts';
export * from './physics/index.ts';
export * from './gameplay/index.ts';
