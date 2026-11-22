/**
 * Touch gestures as menu navigation, for a screen without buttons (a phone used as the controller
 * of a game on a TV): a drag steps the focus (one step per `stepDistance` travelled, so a long drag
 * scrolls), a tap confirms, a two-finger tap goes back. Positions in px (y down), times in ms.
 * Pure: feed it the pointers, read the intents.
 */

export type MenuGestureIntent = 'up' | 'down' | 'left' | 'right' | 'confirm' | 'back';

export interface MenuGestureOptions {
  /** Travel per focus step (default 48). */
  stepDistance?: number;
  /** A finger that moves less stays a tap (default 14). */
  tapSlop?: number;
  /** A tap lifts within this (default 350). */
  tapMs?: number;
}

interface Finger {
  x0: number;
  y0: number;
  /** Where the next step is measured from. */
  ax: number;
  ay: number;
  t0: number;
  moved: boolean;
}

export class MenuGestures {
  private readonly fingers = new Map<number, Finger>();
  /** Two or more fingers were down at once since the last time all lifted. */
  private chord = false;
  /** A finger of the current touch moved or stayed too long: no tap when the last one lifts. */
  private spoiled = false;
  private readonly step: number;
  private readonly slop: number;
  private readonly tapMs: number;

  constructor(options: MenuGestureOptions = {}) {
    this.step = options.stepDistance ?? 48;
    this.slop = options.tapSlop ?? 14;
    this.tapMs = options.tapMs ?? 350;
  }

  down(id: number, x: number, y: number, t: number): void {
    this.fingers.set(id, { x0: x, y0: y, ax: x, ay: y, t0: t, moved: false });
    if (this.fingers.size > 1) this.chord = true;
  }

  /** The focus steps a drag made (none for the fingers of a two-finger touch). */
  move(id: number, x: number, y: number): MenuGestureIntent[] {
    const f = this.fingers.get(id);
    if (!f) return [];
    if (!f.moved && Math.hypot(x - f.x0, y - f.y0) > this.slop) {
      f.moved = true;
      this.spoiled = true;
    }
    if (this.chord) return [];
    const out: MenuGestureIntent[] = [];
    for (;;) {
      const dx = x - f.ax;
      const dy = y - f.ay;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < this.step) return out;
      if (Math.abs(dx) >= Math.abs(dy)) {
        out.push(dx > 0 ? 'right' : 'left');
        f.ax += Math.sign(dx) * this.step;
        f.ay = y;
      } else {
        out.push(dy > 0 ? 'down' : 'up');
        f.ay += Math.sign(dy) * this.step;
        f.ax = x;
      }
    }
  }

  /** A tap (confirm) or a two-finger tap (back) when the last finger of the touch lifts. */
  up(id: number, t: number): MenuGestureIntent[] {
    const f = this.fingers.get(id);
    if (!f) return [];
    this.fingers.delete(id);
    if (t - f.t0 > this.tapMs) this.spoiled = true;
    if (this.fingers.size > 0) return [];
    const intent: MenuGestureIntent | null = this.spoiled ? null : this.chord ? 'back' : 'confirm';
    this.chord = false;
    this.spoiled = false;
    return intent ? [intent] : [];
  }

  /** Every finger lifts without a gesture (the screen changed). */
  cancel(): void {
    this.fingers.clear();
    this.chord = false;
    this.spoiled = false;
  }
}
