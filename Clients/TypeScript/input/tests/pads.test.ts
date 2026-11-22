import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { hasTouchpad, identifyPad, parseTouchpadReport, TOUCHPAD_SIZE, touchpadModelOf } from '../src/index.ts';

/** A touch point's 4 bytes: [inactive bit | id], x (12 bits), y (12 bits). */
function touchBytes(id: number, x: number, y: number, down = true): number[] {
  return [(down ? 0 : 0x80) | id, x & 0xff, ((x >> 8) & 0x0f) | ((y & 0x0f) << 4), y >> 4];
}

/** A report body (without the report id) with the common block at `at`. */
function report(length: number, at: number, sticks: number[], touches: number[][], touchAt = 32): DataView {
  const bytes = new Uint8Array(length);
  bytes.set(sticks, at);
  touches.forEach((t, i) => bytes.set(t, at + touchAt + i * 4));
  return new DataView(bytes.buffer);
}

const NO_TOUCH = touchBytes(0, 0, 0, false);

describe('touchpad HID reports', () => {
  it('reads a USB report: sticks and a finger', () => {
    const r = parseTouchpadReport('dualsense', 0x01, report(63, 0, [255, 0, 128, 128], [touchBytes(5, 1500, 900), NO_TOUCH]));
    assert.ok(r?.full);
    assert.equal(r.sticks.lx, 1);
    assert.equal(r.sticks.ly, -1);
    assert.ok(Math.abs(r.sticks.rx) < 0.01);
    assert.deepEqual(r.touches, [{ id: 5, x: 1500, y: 900 }]);
  });

  it('reads a Bluetooth full report one byte further in', () => {
    const r = parseTouchpadReport('dualsense', 0x31, report(77, 1, [128, 128, 128, 128], [touchBytes(1, 10, 20), touchBytes(2, TOUCHPAD_SIZE.dualsense.width - 1, TOUCHPAD_SIZE.dualsense.height - 1)]));
    assert.deepEqual(r?.touches, [
      { id: 1, x: 10, y: 20 },
      { id: 2, x: 1919, y: 1079 },
    ]);
  });

  it('a lifted finger is not a contact', () => {
    const r = parseTouchpadReport('dualsense', 0x01, report(63, 0, [128, 128, 128, 128], [NO_TOUCH, NO_TOUCH]));
    assert.deepEqual(r?.touches, []);
  });

  it('the Bluetooth simple report has sticks but no touchpad', () => {
    const r = parseTouchpadReport('dualsense', 0x01, report(9, 0, [0, 128, 128, 128], []));
    assert.equal(r?.full, false);
    assert.equal(r?.sticks.lx, -1);
    assert.deepEqual(r?.touches, []);
  });

  it('reads a DualShock 4 over USB and Bluetooth (touch points after the count and timestamp)', () => {
    const usb = parseTouchpadReport('dualshock4', 0x01, report(63, 0, [128, 255, 128, 128], [touchBytes(3, 800, 941), NO_TOUCH], 34));
    assert.equal(usb?.sticks.ly, 1);
    assert.deepEqual(usb?.touches, [{ id: 3, x: 800, y: 941 }]);
    const bt = parseTouchpadReport('dualshock4', 0x11, report(77, 2, [128, 128, 128, 128], [NO_TOUCH, touchBytes(9, 4, 5)], 34));
    assert.deepEqual(bt?.touches, [{ id: 9, x: 4, y: 5 }]);
    assert.equal(parseTouchpadReport('dualshock4', 0x31, report(77, 1, [], [])), null);
  });

  it('knows which pads have a readable touchpad', () => {
    assert.equal(touchpadModelOf(0x054c, 0x0ce6), 'dualsense');
    assert.equal(touchpadModelOf(0x054c, 0x09cc), 'dualshock4');
    assert.equal(touchpadModelOf(0x045e, 0x0b13), null);
  });

  it('ignores other reports', () => {
    assert.equal(parseTouchpadReport('dualsense', 0x05, report(40, 0, [], [])), null);
    assert.equal(parseTouchpadReport('dualsense', 0x31, report(20, 0, [], [])), null);
  });
});

describe('controller detection', () => {
  it('names the brand of each maker', () => {
    assert.equal(identifyPad('DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)').brand, 'playstation');
    assert.equal(identifyPad('Xbox 360 Controller (XInput STANDARD GAMEPAD)').brand, 'xbox');
    assert.equal(identifyPad('Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)').brand, 'nintendo');
    assert.equal(identifyPad('Steam Virtual Gamepad (Vendor: 28de Product: 11ff)').brand, 'steam');
    assert.equal(identifyPad('8BitDo SN30 Pro (Vendor: 2dc8 Product: 6101)').brand, null);
  });

  it('DualSense, DualSense Edge and DualShock 4 have a readable touchpad', () => {
    assert.ok(hasTouchpad(identifyPad('Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)')));
    assert.ok(hasTouchpad(identifyPad('054c-0df2-DualSense Edge Wireless Controller')));
    assert.ok(hasTouchpad(identifyPad('Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 09cc)')));
    assert.ok(!hasTouchpad(identifyPad('PLAYSTATION(R)3 Controller (STANDARD GAMEPAD Vendor: 054c Product: 0268)')));
    assert.ok(!hasTouchpad(identifyPad('Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)')));
    assert.ok(!hasTouchpad(identifyPad('Steam Deck (Vendor: 28de Product: 1205)')));
  });
});
