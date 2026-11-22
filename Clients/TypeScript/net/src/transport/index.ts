/**
 * `@altruist/net/transport` — the WebSocket connection ({@link AltruistSocket}: tickets, hello,
 * reconnect with backoff and close-code policy, heartbeat, fleet redirect, packet routing) and
 * where it connects ({@link ticketUrl}, {@link withNode}, {@link EndpointFailover}).
 */
export * from './endpoint.ts';
export * from './socket.ts';
