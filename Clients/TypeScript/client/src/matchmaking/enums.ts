/**
 * The match-hosting enums of the Altruist server, mirrored for clients (C# `Altruist.Gaming`):
 * names are the C# member names in camelCase and the array index is the C# numeric value, so a
 * game may send either on the wire (`queueStateName(1)` and `queueStateName('searching')` agree).
 *
 * Games usually extend the queue states with their own (e.g. "banned", "concluded"): those are
 * plain strings next to these; see {@link QueueStatusInfo}.
 */

/** C# `QueueState`: not queued, waiting in a queue, a room or seat was found. Index = numeric value. */
export const QUEUE_STATES = ['idle', 'searching', 'found'] as const;
/** A name of {@link QUEUE_STATES}. */
export type QueueStateName = (typeof QUEUE_STATES)[number];

/** C# `LobbyRejectReason`: no lobby has the code, it is full, the server cannot take it (draining / full), more members than the mode's seats. */
export const LOBBY_REJECT_REASONS = ['notFound', 'full', 'unavailable', 'tooManyPlayers'] as const;
/** A name of {@link LOBBY_REJECT_REASONS}. */
export type LobbyRejectReasonName = (typeof LOBBY_REJECT_REASONS)[number];

/** C# `RoomNoticeKind`: a queued player joining a seat in progress, a player left, a bot took a seat, a bot handed it over, a player reconnected. */
export const ROOM_NOTICE_KINDS = ['joining', 'left', 'botIn', 'botOut', 'reconnected'] as const;
/** A name of {@link ROOM_NOTICE_KINDS}. */
export type RoomNoticeKindName = (typeof ROOM_NOTICE_KINDS)[number];

function nameOf<T extends string>(names: readonly T[], value: unknown): T | null {
  if (typeof value === 'number') return Number.isInteger(value) ? (names[value] ?? null) : null;
  if (typeof value !== 'string') return null;
  if ((names as readonly string[]).includes(value)) return value as T;
  // Tolerate PascalCase (C# ToString()) and snake_case.
  const camel = value.replace(/_([a-z])/g, (_, c: string) => c.toUpperCase());
  const lower = camel.charAt(0).toLowerCase() + camel.slice(1);
  return (names as readonly string[]).includes(lower) ? (lower as T) : null;
}

/** The {@link QueueStateName} of a wire value (number, camelCase, PascalCase or snake_case), or null. */
export function queueStateName(value: unknown): QueueStateName | null {
  return nameOf(QUEUE_STATES, value);
}

/** The {@link LobbyRejectReasonName} of a wire value, or null. */
export function lobbyRejectReasonName(value: unknown): LobbyRejectReasonName | null {
  return nameOf(LOBBY_REJECT_REASONS, value);
}

/** The {@link RoomNoticeKindName} of a wire value, or null. */
export function roomNoticeKindName(value: unknown): RoomNoticeKindName | null {
  return nameOf(ROOM_NOTICE_KINDS, value);
}

/**
 * A connection's queue status (C# `QueueStatusInfo`). `state` is a {@link QueueStateName} or a
 * game-defined state (`S`).
 */
export interface QueueStatusInfo<S extends string = QueueStateName> {
  state: QueueStateName | S;
  /** Playlist id ("" when unknown). */
  playlist: string;
  /** Seconds waited so far. */
  elapsed: number;
  /** Players waiting in that queue, capped at `playersNeeded`. */
  playersFound: number;
  /** Players in a full room of the playlist. */
  playersNeeded: number;
}

/** A room notice (C# `IRoomGame.Notice(room, kind, seat, name)`). */
export interface RoomNotice {
  kind: RoomNoticeKindName;
  /** The seat it happened to. */
  seat: number;
  /** Display name of the player or bot. */
  name: string;
}
