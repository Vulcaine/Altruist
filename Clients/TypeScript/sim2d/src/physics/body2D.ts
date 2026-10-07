/**
 * Physics layer. The minimal structural body the helpers need. A planck.js `Body` satisfies every
 * interface here as is (no wrapper), as do other engines' bodies with the same method names.
 * Rotation: counter-clockwise radians, local +Y is the body's "up" (Box2D / planck).
 */
import type { Vec2Like } from '../math/vec2.ts';

/** Reads and writes linear velocity. */
export interface LinearBody {
  getLinearVelocity(): Vec2Like;
  setLinearVelocity(v: Vec2Like): void;
}

/** Reads the angle and reads/writes angular velocity. */
export interface AngularBody {
  getAngle(): number;
  getAngularVelocity(): number;
  setAngularVelocity(w: number): void;
}

export interface PositionedBody {
  getPosition(): Vec2Like;
}

/** A full rigid body: everything above plus transforms and frames. */
export interface Body2DLike extends LinearBody, AngularBody, PositionedBody {
  setTransform(position: Vec2Like, angle: number): void;
  getWorldCenter(): Vec2Like;
  getLocalPoint(worldPoint: Vec2Like): Vec2Like;
  getWorldVector(localVector: Vec2Like): Vec2Like;
  isAwake(): boolean;
  setAwake(flag: boolean): void;
}

/** A body that can be switched off (planck: `isActive` / `setActive`). */
export interface ActivatableBody {
  isActive(): boolean;
  setActive(flag: boolean): void;
}

/** A world that casts rays (planck `World.rayCast`). The callback returns -1 to ignore the
 * fixture, 0 to stop, the fraction to clip the ray, 1 to continue. */
export interface RayCastWorld<F> {
  rayCast(from: Vec2Like, to: Vec2Like, callback: (fixture: F, point: Vec2Like, normal: Vec2Like, fraction: number) => number): void;
}
