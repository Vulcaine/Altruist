/**
 * `@altruist/client/matchmaking` — Altruist's match-hosting enums ({@link QUEUE_STATES},
 * {@link LOBBY_REJECT_REASONS}, {@link ROOM_NOTICE_KINDS}), the {@link MatchmakingClient} phase
 * machine (queue / rejoin / lobby / match / post over an AltruistSocket, game-defined packets) and
 * the {@link StatusPoller} for rejoin / ban status endpoints.
 */
export * from './enums.ts';
export * from './matchmakingClient.ts';
export * from './statusPoller.ts';
