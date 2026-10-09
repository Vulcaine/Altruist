/**
 * @altruist/input — client twin of Altruist's room input contracts.
 *
 *   RoomInputModel   presses, canonical stage order, merges (C# `RoomInputModel<TInput>`)
 *   InputBuffer      the server's sequenced input buffer, for local use (C# `InputBuffer<TInput>`)
 *   InputSequencer   device changes -> one canonical input per step (client-only)
 *   StateMerger      several devices of one player -> one change stream (client-only)
 *   PauseDetector    resync after menus (client-only)
 *   InputSendQueue   sequence numbers, redundant packets, acks (client side of `InputBuffer`)
 *   InputPacer       input clock pacing toward the server's target depth
 *   ButtonField      multi-bit values packed into the button word
 *   quantize         wire-exact axis quantization (`@altruist/sim2d` `Scalar.quantize`)
 *   pads             controller detection (Gamepad.id -> family, brand, model), raw layouts ->
 *                    the W3C standard mapping, PlayStation touchpads in raw HID reports (WebHID)
 */
export * from './roomInputModel.ts';
export * from './inputBuffer.ts';
export * from './inputSequencer.ts';
export * from './inputSendQueue.ts';
export * from './buttonField.ts';
export * from './quantize.ts';
export * from './pads/padIdentity.ts';
export * from './pads/padRemap.ts';
export * from './pads/touchpadHid.ts';
