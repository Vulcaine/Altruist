/**
 * Device pairing (C# `PairingPortal`): {@link PairingHost} opens a session and shows its code,
 * {@link PairingCompanion} joins it; each link uses WebRTC on the local network when it can and the
 * server relay otherwise, with in-order delivery across both.
 */
export * from './packets.ts';
export * from './peerLink.ts';
export * from './pairingHost.ts';
export * from './pairingCompanion.ts';
