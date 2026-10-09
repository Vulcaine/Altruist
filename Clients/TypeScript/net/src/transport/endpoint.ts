/**
 * Where a socket connects: connection tickets, fleet node routing and direct-host failover.
 *
 * - {@link ticketUrl}: a URL provider for {@link AltruistSocket} that fetches a fresh one-time
 *   connection ticket for every connect (Altruist `IConnectionTicketService` / `[TicketShield]`:
 *   browsers cannot set an Authorization header on a WebSocket, so the client trades its access
 *   token for a short-lived ticket and connects with `?ticket=`). Tickets are single-use, so the
 *   provider is called again on every reconnect and redirect.
 * - {@link withNode}: the URL for a connection that belongs to a given fleet server (C#
 *   `RoomRedirect`: `?{nodeParam}={nodeId}` through the shared address, or the server's own
 *   `publicAddress`).
 * - {@link EndpointFailover}: prefer a direct game host (lower latency than a CDN / tunnel hop) and
 *   fall back to the page's own origin when it cannot be reached, remembering the failure for a while.
 */
import { browserStorage, type StorageLike } from '../util/jsonStore.ts';

/**
 * A server's instruction to move to another server of the fleet (C# `RoomRedirect`). Decode it
 * from your game's redirect packet and pass it to {@link AltruistSocket.redirect}.
 */
export interface RoomRedirect {
  /** Target server's node id. */
  nodeId: string;
  /** Query parameter the load balancer routes on (the fleet's `node-param`, usually "node"). */
  nodeParam: string;
  /** Target server's own public address, or null (go through the shared address). */
  publicAddress: string | null;
  /** "rejoin", "queue", "lobby" or "drain" (informational). */
  reason: string;
}

/**
 * The URL for a connection that belongs to `node`: its own public address when it has one (the
 * query string of `url`, e.g. the ticket, is kept), else `url` itself; either way with
 * `?{nodeParam}={nodeId}` set. Returns `url` unchanged for a null node.
 */
export function withNode(url: string, node: RoomRedirect | null): string {
  if (!node) return url;
  const base = new URL(url);
  const target = node.publicAddress ? new URL(node.publicAddress) : base;
  if (node.publicAddress) target.search = base.search;
  target.searchParams.set(node.nodeParam || 'node', node.nodeId);
  return target.toString();
}

/** Options of {@link ticketUrl}. */
export interface TicketUrlOptions {
  /** Socket URL without the ticket, or a function that picks it per connect (e.g. {@link EndpointFailover.current}). */
  base: string | (() => string);
  /**
   * Fetches a ticket from your authenticated endpoint (Altruist `ConnectionTicket`:
   * `{ ticket, expiresInSeconds }`); a bare string is accepted too. Errors propagate (an HTTP
   * error with `status` 401 / 403 ends a socket's reconnects).
   */
  fetchTicket: () => Promise<{ ticket: string } | string>;
  /** Query parameter (`altruist:security:tickets:query-param`, default "ticket"). */
  queryParam?: string;
  /** Connect without a ticket when the endpoint answers 404 (servers without tickets; default false). */
  allowMissingEndpoint?: boolean;
}

/**
 * A URL provider that adds a fresh ticket to every connect. Pass it as {@link AltruistSocket}'s `url`.
 *
 * @example
 * ```ts
 * const url = ticketUrl({ base: 'wss://play.example.com/game', fetchTicket: () => http.post('/game/ticket') });
 * const socket = new AltruistSocket({ url });
 * ```
 */
export function ticketUrl(options: TicketUrlOptions): () => Promise<string> {
  const param = options.queryParam ?? 'ticket';
  return async () => {
    const base = typeof options.base === 'function' ? options.base() : options.base;
    let ticket: string;
    try {
      const t = await options.fetchTicket();
      ticket = typeof t === 'string' ? t : t.ticket;
    } catch (err) {
      if (options.allowMissingEndpoint && (err as { status?: unknown } | null)?.status === 404) return base;
      throw err;
    }
    const url = new URL(base);
    url.searchParams.set(param, ticket);
    return url.toString();
  };
}

/** The parts of `window.location` the helpers read. */
export interface PageLocation {
  protocol: string;
  host: string;
  hostname: string;
}

const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]', '0.0.0.0']);

/** `ws(s)://{page host}{path}` — the socket on the page's own origin (a dev proxy or the same server). */
export function sameOriginSocketUrl(path: string, loc: PageLocation = globalThis.location): string {
  return `${loc.protocol === 'https:' ? 'wss' : 'ws'}://${loc.host}${path.startsWith('/') ? path : `/${path}`}`;
}

/**
 * Validates a configured direct socket URL for this page: null when empty, not ws/wss, `ws:` from
 * an https page (mixed content), the page's own host, or when the page itself is local (dev).
 */
export function directSocketUrl(raw: string | null | undefined, loc: PageLocation | undefined = globalThis.location): string | null {
  const value = raw?.trim();
  if (!value || !loc || LOCAL_HOSTS.has(loc.hostname)) return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (url.protocol !== 'wss:' && url.protocol !== 'ws:') return null;
  if (loc.protocol === 'https:' && url.protocol !== 'wss:') return null;
  if (url.host === loc.host) return null;
  return url.toString();
}

/** Options of {@link EndpointFailover}. */
export interface EndpointFailoverOptions {
  /** The preferred (direct) URL, or null for none (then {@link EndpointFailover.current} is always the fallback). */
  primary: string | null;
  /** Where to connect when the primary is down. */
  fallback: string;
  /** How long a failed primary is skipped (ms, default 10 min). */
  retryAfterMs?: number;
  /** A primary socket that has not opened after this long counts as failed (ms, default 4000). */
  connectTimeoutMs?: number;
  /** Remember the failure across reloads of this tab under this key (default: memory only). */
  storageKey?: string;
  /** Default sessionStorage when `storageKey` is set. */
  storage?: StorageLike | null;
  now?: () => number;
}

/**
 * Prefer a primary socket host and fall back to another, remembering a failure for
 * `retryAfterMs`. {@link AltruistSocket} uses it (`failover` option): a primary socket that never
 * opens marks it down and the same connect is retried once on the fallback at once.
 *
 * @example
 * ```ts
 * const failover = new EndpointFailover({
 *   primary: directSocketUrl(import.meta.env.VITE_GAME_WS_URL),
 *   fallback: sameOriginSocketUrl('/game'),
 *   storageKey: 'mygame.directDownAt',
 * });
 * const socket = new AltruistSocket({ url: ticketUrl({ base: () => failover.current(), fetchTicket }), failover });
 * ```
 */
export class EndpointFailover {
  private downAt = -Infinity;
  private readonly storage: StorageLike | null;
  readonly retryAfterMs: number;
  readonly connectTimeoutMs: number;

  private readonly options: EndpointFailoverOptions;

  /** Creates the selector (a stored failure is read once). */
  constructor(options: EndpointFailoverOptions) {
    this.options = options;
    this.retryAfterMs = options.retryAfterMs ?? 10 * 60_000;
    this.connectTimeoutMs = options.connectTimeoutMs ?? 4000;
    this.storage = options.storageKey ? (options.storage === undefined ? browserStorage('session') : options.storage) : null;
    if (this.storage && options.storageKey) {
      try {
        const v = Number(this.storage.getItem(options.storageKey));
        if (v) this.downAt = v;
      } catch {
        /* blocked storage */
      }
    }
  }

  private now(): number {
    return (this.options.now ?? Date.now)();
  }

  /** The URL the next connect should use. */
  current(): string {
    if (this.options.primary && this.now() - this.downAt >= this.retryAfterMs) return this.options.primary;
    return this.options.fallback;
  }

  /** Whether `url` points at the primary host (any path / query). */
  isPrimary(url: string): boolean {
    if (!this.options.primary) return false;
    try {
      return new URL(url).host === new URL(this.options.primary).host;
    } catch {
      return false;
    }
  }

  /** The primary failed: skip it for a while. True when this changed where the next connect goes. */
  markDown(): boolean {
    if (!this.options.primary) return false;
    const now = this.now();
    const changed = now - this.downAt >= this.retryAfterMs;
    this.downAt = now;
    if (this.storage && this.options.storageKey) {
      try {
        this.storage.setItem(this.options.storageKey, String(now));
      } catch {
        /* remembered for this page load only */
      }
    }
    return changed;
  }

  /** Forgets a recorded failure. */
  reset(): void {
    this.downAt = -Infinity;
    if (this.storage && this.options.storageKey) {
      try {
        this.storage.removeItem(this.options.storageKey);
      } catch {
        /* ignore */
      }
    }
  }
}
