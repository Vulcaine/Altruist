import type { PadIdentity } from './padIdentity.ts';

/**
 * Normalises raw Gamepad buttons/axes into the W3C "standard" layout the rest of the input code
 * uses. Chrome, Edge and Safari report mapping === 'standard' for Xbox, PlayStation and Switch Pro
 * pads, so they pass straight through. The tables below cover the raw layouts browsers still
 * expose (checked against Firefox's dom/gamepad remappers, the Linux gamepad/evdev protocol and
 * SDL's controller database):
 *   - Firefox on Windows/macOS has no remapper for the DualSense: raw HID button order.
 *   - Linux evdev order (Chrome for pads it has no table for, older Firefox): xpad / hid-sony /
 *     hid-playstation, triggers and D-pad as axes.
 *   - Xbox pads over Bluetooth with newer firmware ids on macOS Firefox: HID order + hat switch.
 *   - Firefox on Linux marks every evdev pad 'standard' but leaves xpad's analog triggers on
 *     axes 4/5 (no digital LT/RT), so those get folded back into buttons 6/7.
 * Pure, unit-tested.
 */

/** Standard mapping indices. */
export const STD = {
  south: 0,
  east: 1,
  west: 2,
  north: 3,
  lb: 4,
  rb: 5,
  lt: 6,
  rt: 7,
  view: 8,
  menu: 9,
  ls: 10,
  rs: 11,
  up: 12,
  down: 13,
  left: 14,
  right: 15,
  home: 16,
  touchpad: 17,
} as const;
export const STD_BUTTON_COUNT = 18;

export interface PadLayout {
  id: 'standard' | 'firefox-linux' | 'sony-hid' | 'sony-evdev' | 'xinput-evdev' | 'xinput-evdev-dpad' | 'xbox-bt-hid' | 'raw';
  /** Shown in settings ("Remapped: ..."). */
  label: string;
  /** Standard index -> raw button index (undefined = not a button on this layout); null = as-is. */
  buttons: readonly (number | undefined)[] | null;
  /** Raw axes carrying LT / RT (-1 released .. 1 fully pulled), folded into buttons 6 / 7. */
  triggerAxes?: readonly [number, number];
  /** Raw axes carrying the D-pad as two -1/0/1 axes (evdev ABS_HAT0X/Y), folded into 12-15. */
  hatAxes?: readonly [number, number];
  /** Look for a single hat-switch axis (rests above 1, -1 = up, clockwise in 2/7 steps). */
  hatSwitch: boolean;
  /** Raw axes of the right stick (x, y; y down). Unset = unknown (no right stick reported). */
  rightAxes?: readonly [number, number];
}

export interface LayoutChoice {
  layout: PadLayout;
  /** False: no known table (or too few inputs); the pad runs best-effort and the player is told. */
  supported: boolean;
}

/** Shape of a pad as the browser reports it. */
export interface PadShape {
  id: string;
  mapping: string;
  buttons: number;
  axes: number;
}

const u = undefined;

export const LAYOUTS = {
  standard: { id: 'standard', label: 'Standard', buttons: null, hatSwitch: false, rightAxes: [2, 3] },
  firefoxLinux: { id: 'firefox-linux', label: 'Standard, analog triggers', buttons: null, triggerAxes: [4, 5], hatSwitch: false, rightAxes: [2, 3] },
  // Raw 0 Square, 1 Cross, 2 Circle, 3 Triangle, 4 L1, 5 R1, 6 L2, 7 R2, 8 Create/Share, 9 Options,
  // 10 L3, 11 R3, 12 PS, 13 Touchpad (14 Mute); D-pad on a hat-switch axis.
  sonyHid: { id: 'sony-hid', label: 'PlayStation HID', buttons: [1, 2, 0, 3, 4, 5, 6, 7, 8, 9, 10, 11, u, u, u, u, 12, 13], hatSwitch: true, rightAxes: [2, 5] },
  // Raw 0 Cross, 1 Circle, 2 Triangle, 3 Square, 4 L1, 5 R1, 6 L2, 7 R2, 8 Share, 9 Options, 10 PS,
  // 11 L3, 12 R3; axes 0-1 left stick, 2 L2, 3-4 right stick, 5 R2, 6-7 D-pad.
  sonyEvdev: { id: 'sony-evdev', label: 'PlayStation (Linux)', buttons: [0, 1, 3, 2, 4, 5, 6, 7, 8, 9, 11, 12, u, u, u, u, 10], hatAxes: [6, 7], hatSwitch: false, rightAxes: [3, 4] },
  // Raw 0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back, 7 Start, 8 Guide, 9 LS, 10 RS; axes 0-1 left stick,
  // 2 LT, 3-4 right stick, 5 RT, 6-7 D-pad.
  xinputEvdev: { id: 'xinput-evdev', label: 'XInput (Linux)', buttons: [0, 1, 2, 3, 4, 5, u, u, 6, 7, 9, 10, u, u, u, u, 8], triggerAxes: [2, 5], hatAxes: [6, 7], hatSwitch: false, rightAxes: [3, 4] },
  // xpad's Xbox 360 wireless receiver: same, but the D-pad is buttons 11-14 (left, right, up, down).
  xinputEvdevDpad: { id: 'xinput-evdev-dpad', label: 'XInput (Linux)', buttons: [0, 1, 2, 3, 4, 5, u, u, 6, 7, 9, 10, 13, 14, 11, 12, 8], triggerAxes: [2, 5], hatSwitch: false, rightAxes: [3, 4] },
  // Raw 0 A, 1 B, 3 X, 4 Y, 6 LB, 7 RB, 10 View, 11 Menu, 12 Guide, 13 LS, 14 RS (gaps are unused HID
  // usages); axes 0-1 left stick, 2-3 right stick, 4 LT (brake), 5 RT (accelerator), then the hat.
  xboxBtHid: { id: 'xbox-bt-hid', label: 'Xbox Bluetooth HID', buttons: [0, 1, 3, 4, 6, 7, u, u, 10, 11, 13, 14, u, u, u, u, 12], triggerAxes: [4, 5], hatSwitch: true, rightAxes: [2, 3] },
  raw: { id: 'raw', label: 'Unknown layout', buttons: null, hatSwitch: true },
} as const satisfies Record<string, PadLayout>;

export function chooseLayout(who: PadIdentity, pad: PadShape): LayoutChoice {
  const ok = (layout: PadLayout): LayoutChoice => ({ layout, supported: true });
  if (pad.buttons < 4 || pad.axes < 2) return { layout: pad.mapping === 'standard' ? LAYOUTS.standard : LAYOUTS.raw, supported: false };
  if (pad.mapping === 'standard') {
    // Firefox/Linux id format ("045e-028e-...") with exactly the 4 stick axes + ABS_Z / ABS_RZ.
    const firefoxLinux = /^[0-9a-f]{4}-[0-9a-f]{4}-/i.test(pad.id) && pad.axes === 6 && who.family === 'xbox';
    return ok(firefoxLinux ? LAYOUTS.firefoxLinux : LAYOUTS.standard);
  }
  if (who.family === 'playstation') {
    if (pad.buttons >= 14) return ok(LAYOUTS.sonyHid);
    if (pad.buttons === 13 && pad.axes >= 8) return ok(LAYOUTS.sonyEvdev);
  }
  if (who.family === 'xbox') {
    if (pad.buttons === 15 && pad.axes === 6) return ok(LAYOUTS.xinputEvdevDpad);
    if (pad.buttons >= 11 && pad.buttons <= 12 && pad.axes === 8) return ok(LAYOUTS.xinputEvdev);
    if (pad.buttons >= 15 && pad.axes >= 6) return ok(LAYOUTS.xboxBtHid);
  }
  return { layout: LAYOUTS.raw, supported: false };
}

export interface RawButton {
  pressed: boolean;
  value: number;
}

/** Per-pad memory: which axis turned out to be a hat switch (-1 = none seen yet). */
export interface PadRemapState {
  hatAxis: number;
}

export interface PadSnapshot {
  /** Standard button states, STD_BUTTON_COUNT long. */
  pressed: boolean[];
  /** Left stick, raw browser convention (y down). */
  x: number;
  y: number;
  /** Right stick, same convention (0 when the layout has none). */
  rx: number;
  ry: number;
}

/** Analog trigger travel (0..1) that counts as pressed. */
const TRIGGER_PRESS = 0.55;
const AXIS_PRESS = 0.5;

export function normalizePad(layout: PadLayout, buttons: readonly RawButton[], axes: readonly number[], state: PadRemapState): PadSnapshot {
  const down = (i: number | undefined) => {
    const b = i === undefined ? undefined : buttons[i];
    return !!b && (b.pressed || b.value > 0.5);
  };
  const pressed: boolean[] = [];
  const n = layout.buttons ? STD_BUTTON_COUNT : Math.max(STD_BUTTON_COUNT, buttons.length);
  for (let i = 0; i < n; i++) pressed.push(down(layout.buttons ? layout.buttons[i] : i));

  if (layout.hatSwitch && state.hatAxis < 0) {
    // A hat switch rests outside [-1, 1]; sticks and triggers never do.
    for (let i = 2; i < axes.length; i++) {
      if (Math.abs(axes[i] ?? 0) > 1.05) {
        state.hatAxis = i;
        break;
      }
    }
  }
  if (state.hatAxis >= 0) {
    const dir = hatDirection(axes[state.hatAxis] ?? 2);
    if (dir >= 0) {
      pressed[STD.up] ||= dir === 7 || dir <= 1;
      pressed[STD.right] ||= dir >= 1 && dir <= 3;
      pressed[STD.down] ||= dir >= 3 && dir <= 5;
      pressed[STD.left] ||= dir >= 5 && dir <= 7;
    }
  }
  if (layout.hatAxes) {
    const hx = axes[layout.hatAxes[0]] ?? 0;
    const hy = axes[layout.hatAxes[1]] ?? 0;
    pressed[STD.left] ||= hx < -AXIS_PRESS;
    pressed[STD.right] ||= hx > AXIS_PRESS;
    pressed[STD.up] ||= hy < -AXIS_PRESS;
    pressed[STD.down] ||= hy > AXIS_PRESS;
  }
  if (layout.triggerAxes) {
    const pull = (i: number) => {
      const v = axes[i];
      // Never a hat switch (out of range) misread as a trigger.
      if (v === undefined || i === state.hatAxis || Math.abs(v) > 1.05) return false;
      return (v + 1) / 2 > TRIGGER_PRESS;
    };
    pressed[STD.lt] ||= pull(layout.triggerAxes[0]);
    pressed[STD.rt] ||= pull(layout.triggerAxes[1]);
  }
  // The right stick (never a hat switch or an out-of-range axis misread as one).
  const stickAxis = (i: number | undefined) => {
    const v = i === undefined ? undefined : axes[i];
    return v === undefined || i === state.hatAxis || !Number.isFinite(v) || Math.abs(v) > 1.05 ? 0 : v;
  };
  return { pressed, x: axes[0] ?? 0, y: axes[1] ?? 0, rx: stickAxis(layout.rightAxes?.[0]), ry: stickAxis(layout.rightAxes?.[1]) };
}

/**
 * Hat-switch axis value -> direction 0 up, 1 up-right ... 7 up-left, or -1 when centred. Browsers
 * spread the 8 directions over -1..1 in 2/7 steps and park the released hat above 1 (Firefox
 * sometimes reports exactly 0 before the first event).
 */
export function hatDirection(v: number): number {
  if (!Number.isFinite(v) || Math.abs(v) > 1.05 || v === 0) return -1;
  return Math.max(0, Math.min(7, Math.round((v + 1) * 3.5)));
}
