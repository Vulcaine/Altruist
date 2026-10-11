/**
 * PlayStation touchpads in raw HID input reports, for what the Gamepad API leaves out: the finger
 * positions. Layouts from the Linux hid-playstation driver. The report id is not part of `data`
 * (WebHID strips it); the common block (sticks first) starts at:
 *   DualSense    USB id 0x01 (63 bytes) at 0, Bluetooth id 0x31 (77 bytes) at 1;
 *                touch points at block + 32
 *   DualShock 4  USB id 0x01 (63 bytes) at 0, Bluetooth id 0x11 (77 bytes) at 2;
 *                touch points at block + 34 (after the touch report count and timestamp)
 * Over Bluetooth both send a "simple" report (id 0x01, 9 bytes: sticks and buttons, no touchpad)
 * until their calibration feature report is read (FULL_REPORTS_FEATURE).
 * A touch point is 4 bytes: [bit 7 = not touching | 7-bit contact id], x (12 bits), y (12 bits).
 * Pure, unit-tested.
 */

export type TouchpadModel = 'dualsense' | 'dualshock4';

export const SONY_VENDOR_ID = 0x054c;

/** The pads with a readable touchpad (DualSense, DualSense Edge, DualShock 4 v1 / v2 / USB dongle). */
export const TOUCHPAD_PADS: readonly { vendorId: number; productId: number; model: TouchpadModel }[] = [
  { vendorId: SONY_VENDOR_ID, productId: 0x0ce6, model: 'dualsense' },
  { vendorId: SONY_VENDOR_ID, productId: 0x0df2, model: 'dualsense' },
  { vendorId: SONY_VENDOR_ID, productId: 0x05c4, model: 'dualshock4' },
  { vendorId: SONY_VENDOR_ID, productId: 0x09cc, model: 'dualshock4' },
  { vendorId: SONY_VENDOR_ID, productId: 0x0ba0, model: 'dualshock4' },
];

/** WebHID requestDevice filters for every pad in TOUCHPAD_PADS. */
export const TOUCHPAD_HID_FILTERS: readonly { vendorId: number; productId: number }[] = TOUCHPAD_PADS.map(({ vendorId, productId }) => ({ vendorId, productId }));

export function touchpadModelOf(vendorId: number, productId: number): TouchpadModel | null {
  return TOUCHPAD_PADS.find((p) => p.vendorId === vendorId && p.productId === productId)?.model ?? null;
}

/** Reading this feature report switches a Bluetooth pad from the simple to the full input report. */
export const FULL_REPORTS_FEATURE: Readonly<Record<TouchpadModel, number>> = { dualsense: 0x05, dualshock4: 0x02 };

/** Touchpad coordinate range (x left to right, y top to bottom). */
export const TOUCHPAD_SIZE: Readonly<Record<TouchpadModel, { readonly width: number; readonly height: number }>> = {
  dualsense: { width: 1920, height: 1080 },
  dualshock4: { width: 1920, height: 942 },
};

/** A finger on the touchpad, in touchpad units (TOUCHPAD_SIZE), y down. */
export interface TouchContact {
  /** Contact id: changes when a finger lifts and a new one lands. */
  readonly id: number;
  readonly x: number;
  readonly y: number;
}

export interface TouchpadReport {
  /** Sticks -1..1, y down (like the Gamepad API's axes). */
  readonly sticks: { readonly lx: number; readonly ly: number; readonly rx: number; readonly ry: number };
  /** Fingers on the touchpad now, in report order (0 to 2). Empty for the Bluetooth simple report. */
  readonly touches: readonly TouchContact[];
  /** False for the Bluetooth simple report (no touchpad data until the full reports are on). */
  readonly full: boolean;
}

interface Layout {
  readonly usbId: number;
  readonly btId: number;
  readonly btBlock: number;
  readonly touch: number;
}

const LAYOUTS: Readonly<Record<TouchpadModel, Layout>> = {
  dualsense: { usbId: 0x01, btId: 0x31, btBlock: 1, touch: 32 },
  dualshock4: { usbId: 0x01, btId: 0x11, btBlock: 2, touch: 34 },
};

const USB_LENGTH = 63;
const BT_LENGTH = 77;
const SIMPLE_LENGTH = 9;

const axis = (v: number) => Math.max(-1, Math.min(1, (v - 127.5) / 127.5));

function sticksAt(data: DataView, at: number): TouchpadReport['sticks'] {
  return { lx: axis(data.getUint8(at)), ly: axis(data.getUint8(at + 1)), rx: axis(data.getUint8(at + 2)), ry: axis(data.getUint8(at + 3)) };
}

function touchAt(data: DataView, at: number): TouchContact | null {
  const b0 = data.getUint8(at);
  if (b0 & 0x80) return null;
  const b2 = data.getUint8(at + 2);
  return { id: b0 & 0x7f, x: data.getUint8(at + 1) | ((b2 & 0x0f) << 8), y: (b2 >> 4) | (data.getUint8(at + 3) << 4) };
}

/** The common block's offset in `data`, and whether it has the touchpad, or null for other reports. */
function blockOf(l: Layout, reportId: number, length: number): { at: number; full: boolean } | null {
  if (reportId === l.usbId && length >= USB_LENGTH) return { at: 0, full: true };
  if (reportId === l.btId && length >= BT_LENGTH) return { at: l.btBlock, full: true };
  if (reportId === l.usbId && length >= SIMPLE_LENGTH) return { at: 0, full: false };
  return null;
}

/** Decodes one input report, or null when it is not an input report of this model (feature data, acks). */
export function parseTouchpadReport(model: TouchpadModel, reportId: number, data: DataView): TouchpadReport | null {
  const l = LAYOUTS[model];
  const block = blockOf(l, reportId, data.byteLength);
  if (!block) return null;
  const sticks = sticksAt(data, block.at);
  if (!block.full) return { sticks, touches: [], full: false };
  const first = block.at + l.touch;
  const touches = [touchAt(data, first), touchAt(data, first + 4)].filter((t): t is TouchContact => t !== null);
  return { sticks, touches, full: true };
}
