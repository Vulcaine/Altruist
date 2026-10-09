/**
 * Physics layer. The minimal structural body the helpers need. A planck.js `Body` satisfies every
 * interface here as is (no wrapper), as do other engines' bodies with the same method names.
 * Rotation: counter-clockwise radians, local +Y is the body's "up" (Box2D / planck).
 */
import type { Vec2Like } from '../math/vec2.ts';

/** Reads and writes linear velocity. The TS view of C# `IPhysxBody2D.LinearVelocity`. */
export interface LinearBody {
  /** Current linear velocity in units per second. planck returns its live internal vector, so
   * helpers copy before keeping it. Mirrors the C# `IPhysxBody2D.LinearVelocity` getter. */
  getLinearVelocity(): Vec2Like;
  /** Sets the linear velocity (units per second). On planck this wakes a sleeping body when `v` is
   * non-zero and is ignored by static bodies. Mirrors the C# `IPhysxBody2D.LinearVelocity` setter. */
  setLinearVelocity(v: Vec2Like): void;
}

/** Reads the angle and reads/writes angular velocity. The TS view of C# `IPhysxBody2D.RotationZ` /
 * `AngularVelocityZ`. */
export interface AngularBody {
  /** Rotation in radians, counter-clockwise positive, unwrapped (can exceed ±π). Mirrors C#
   * `IPhysxBody2D.RotationZ`. */
  getAngle(): number;
  /** Angular velocity in radians per second, counter-clockwise positive. Mirrors the C#
   * `IPhysxBody2D.AngularVelocityZ` getter. */
  getAngularVelocity(): number;
  /** Sets the angular velocity (rad/s, CCW positive). On planck this wakes a sleeping body when `w`
   * is non-zero and is ignored by static bodies. Mirrors the C# `IPhysxBody2D.AngularVelocityZ` setter. */
  setAngularVelocity(w: number): void;
}

/** Reads the body origin. The TS view of C# `IPhysxBody2D.Position` (getter). */
export interface PositionedBody {
  /** Body origin in world units (+Y up). planck returns its live internal vector. Mirrors C#
   * `IPhysxBody2D.Position`. */
  getPosition(): Vec2Like;
}

/** A full rigid body: everything above plus transforms and frames. The structural TS twin of C#
 * `IPhysxBody2D`; a planck `Body` fits as is. Prefer the narrowest interface ({@link LinearBody},
 * {@link AngularBody}, ...) in helper signatures so fakes stay small. */
export interface Body2DLike extends LinearBody, AngularBody, PositionedBody {
  /** Moves and rotates the body in one call (angle in radians, CCW). Mirrors C#
   * `IPhysxBody2D.SetTransform`. */
  setTransform(position: Vec2Like, angle: number): void;
  /** Center of mass in world coordinates (may differ from {@link PositionedBody.getPosition}).
   * Mirrors C# `IPhysxBody2D.WorldCenter`. */
  getWorldCenter(): Vec2Like;
  /** A world point in body-local coordinates (local +Y = the body's up). Mirrors C#
   * `IPhysxBody2D.GetLocalPoint`. */
  getLocalPoint(worldPoint: Vec2Like): Vec2Like;
  /** A body-local direction in world coordinates (rotation only, no translation). Mirrors C#
   * `IPhysxBody2D.GetWorldVector`. */
  getWorldVector(localVector: Vec2Like): Vec2Like;
  /** Whether the body is awake (a sleeping body is skipped by the solver). Mirrors the C#
   * `IPhysxBody2D.IsAwake` getter. */
  isAwake(): boolean;
  /** Wakes (`true`) or puts to sleep (`false`) the body. Mirrors the C# `IPhysxBody2D.IsAwake` setter. */
  setAwake(flag: boolean): void;
}

/** A body that can be switched off (planck: `isActive` / `setActive`). The TS view of C#
 * `IPhysxBody2D.IsEnabled`. */
export interface ActivatableBody {
  /** Whether the body takes part in the simulation. Mirrors the C# `IPhysxBody2D.IsEnabled` getter. */
  isActive(): boolean;
  /** Enables or disables the body; on planck toggling destroys / re-creates its contacts. Mirrors
   * the C# `IPhysxBody2D.IsEnabled` setter. */
  setActive(flag: boolean): void;
}

/** A world that casts rays (planck `World.rayCast`). The callback returns -1 to ignore the
 * fixture, 0 to stop, the fraction to clip the ray, 1 to continue. Corresponds to C#
 * `IPhysxWorldEngine2D` ray casting with an `IPhysxRayCastCallback2D`. Use
 * {@link rayCastClosest} rather than writing callbacks by hand.
 * @typeParam F - The engine's fixture type.
 */
export interface RayCastWorld<F> {
  /** Casts a ray from `from` to `to`, calling `callback` for each fixture hit (in no particular
   * order); the callback's return value controls clipping as described on the interface. */
  rayCast(from: Vec2Like, to: Vec2Like, callback: (fixture: F, point: Vec2Like, normal: Vec2Like, fraction: number) => number): void;
}
