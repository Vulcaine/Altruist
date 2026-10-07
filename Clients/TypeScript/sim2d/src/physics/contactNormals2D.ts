/**
 * Physics layer. Sum of a body's contact normals with support (ground) classification — mirror of
 * C# `Altruist.Physx.TwoD.ContactNormals2D`.
 */
import type { Vec2Like } from '../math/vec2.ts';

export class ContactNormals2D {
  allX = 0;
  allY = 0;
  hasAny = false;
  supportX = 0;
  supportY = 0;
  hasSupport = false;

  /** Adds a normal (out of the surface toward the body); it is support when `n·up >= minUpDot`.
   * Without `up` the normal is not tested for support. */
  add(normal: Vec2Like, up?: Vec2Like, minUpDot = 0): void {
    this.hasAny = true;
    this.allX += normal.x;
    this.allY += normal.y;
    if (up && normal.x * up.x + normal.y * up.y >= minUpDot) {
      this.hasSupport = true;
      this.supportX += normal.x;
      this.supportY += normal.y;
    }
  }

  get all(): Vec2Like {
    return { x: this.allX, y: this.allY };
  }

  get support(): Vec2Like {
    return { x: this.supportX, y: this.supportY };
  }

  /** `sum / (|sum| || 1)`. */
  get allDirection(): Vec2Like {
    return direction(this.allX, this.allY);
  }

  get supportDirection(): Vec2Like {
    return direction(this.supportX, this.supportY);
  }
}

function direction(x: number, y: number): Vec2Like {
  const l = Math.sqrt(x * x + y * y) || 1;
  return { x: x / l, y: y / l };
}
