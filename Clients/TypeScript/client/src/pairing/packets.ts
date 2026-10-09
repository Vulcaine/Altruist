/**
 * Wire twin of Altruist's device pairing (C# `PairingPortal`, `PairingPacketCodes`, range 7001-7009):
 * client frames for the `pair-host` / `pair-join` / `pair-send` / `pair-ping` gates and the server
 * packets.
 */
import { packetFrame } from '../../../net/src/wire/envelope.ts';
import { asBytes } from '../../../net/src/wire/msgpack.ts';
import { serverPacket } from '../../../net/src/wire/packetDispatcher.ts';

export const PairingCodes = {
  host: 7001,
  join: 7002,
  send: 7003,
  hosted: 7004,
  joined: 7005,
  peer: 7006,
  message: 7007,
  rejected: 7008,
  ping: 7009,
} as const;

/** C# `PairingRejectReason`, by value (1-based). */
export const PAIRING_REJECT_REASONS = ['unknown-code', 'full', 'already-paired', 'too-many-sessions', 'payload-too-large', 'no-such-peer'] as const;
export type PairingRejectReason = (typeof PAIRING_REJECT_REASONS)[number] | 'unknown';

export function pairingRejectReasonName(value: number): PairingRejectReason {
  return PAIRING_REJECT_REASONS[value - 1] ?? 'unknown';
}

export const hostFrame = () => packetFrame('pair-host', PairingCodes.host);
export const joinFrame = (code: string) => packetFrame('pair-join', PairingCodes.join, code);
/** `to`: 0 = the host, 1.. = a companion slot. */
export const sendFrame = (to: number, payload: Uint8Array) => packetFrame('pair-send', PairingCodes.send, to, payload);
export const pingFrame = () => packetFrame('pair-ping', PairingCodes.ping);

export const HostedPacket = serverPacket(PairingCodes.hosted, (m) => ({ code: String(m[1]), maxCompanions: Number(m[2]) }));
export const JoinedPacket = serverPacket(PairingCodes.joined, (m) => ({ slot: Number(m[1]) }));
export const PeerPacket = serverPacket(PairingCodes.peer, (m) => ({ slot: Number(m[1]), present: m[2] === true }));
export const MessagePacket = serverPacket(PairingCodes.message, (m) => ({ from: Number(m[1]), payload: asBytes(m[2] as Uint8Array) }));
export const RejectedPacket = serverPacket(PairingCodes.rejected, (m) => ({ reason: pairingRejectReasonName(Number(m[1])) }));
