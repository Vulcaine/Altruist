/**
 * `ApiClient` — the HTTP client for an Altruist API with Altruist's token model:
 *
 * - **Access token in memory only** (never storage): sent as `Authorization: Bearer`.
 * - **Refresh token in an HttpOnly cookie** (C# `RefreshCookie`, `altruist:security:refresh-cookie`):
 *   page scripts cannot read it; the browser sends it to the refresh endpoint. Cookie-authenticated
 *   endpoints require a CSRF header (`csrf-header` / `csrf-value`, default `X-Requested-With: 1`),
 *   which this client sends on every request (configure `csrf` to match your server).
 * - **401 → refresh → retry once**: a 401 on an authenticated call triggers one refresh and one
 *   retry. Refreshes are **single-flight**: concurrent callers share one request, and across tabs
 *   a Web Lock (`navigator.locks`, when available) serializes them so two tabs never present the
 *   same rotated cookie (which would trip reuse detection and sign both out).
 * - A refresh that the server definitely rejects (4xx) ends the session (`signedOut` event); a
 *   network failure keeps the current token.
 * - Failures throw {@link ApiError} (status, code, field, retryAfter from Altruist's `simple`
 *   error bodies).
 *
 * Use it for every REST call of a game client; for the realtime socket fetch a connection ticket
 * with {@link ApiClient.connectionTicket} and pass it to `ticketUrl` of `@altruist/net`.
 * Async (fetch); safe to call concurrently.
 */
import { Emitter } from '../../../net/src/util/emitter.ts';
import { browserStorage, type StorageLike } from '../../../net/src/util/jsonStore.ts';
import { ApiError, DEFAULT_ERROR_MESSAGES, toApiError, type ToApiErrorOptions } from './apiError.ts';

/** `fetch`-compatible function. */
export type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

/** The cross-tab lock API (`navigator.locks`). */
export interface LockManagerLike {
  request<T>(name: string, callback: () => Promise<T>): Promise<T>;
}

/** Options of {@link ApiClient}. */
export interface ApiClientOptions {
  /** Prefix of every path (default `/api`). */
  baseUrl?: string;
  fetch?: FetchLike;
  /** Refresh endpoint (POST, cookie-authenticated; default `/auth/refresh`). */
  refreshPath?: string;
  /** Reads the new access token from the refresh (and sign-in) response body (default `body.accessToken`). */
  readToken?: (body: unknown) => string | null | undefined;
  /** CSRF header sent on every request (C# `RefreshCookie.CsrfHeader` / `CsrfValue`); null to send none. Default `X-Requested-With: 1`. */
  csrf?: { header: string; value: string } | null;
  /** Extra headers on every request. */
  headers?: Record<string, string>;
  /** Web Lock name for cross-tab refreshes (default `altruist-auth-refresh`). */
  lockName?: string;
  /** Lock manager (default `navigator.locks` when present; null = in-process single-flight only). */
  locks?: LockManagerLike | null;
  /** Error texts and formatting (see {@link toApiError}). */
  errors?: ToApiErrorOptions;
  /**
   * Remember "this browser had a session" under this storage key, so a guest's page load can skip
   * a refresh that can only fail ({@link ApiClient.hasSessionHint}). Default: no hint.
   */
  sessionHintKey?: string;
  /** Storage for the hint (default localStorage). */
  storage?: StorageLike | null;
}

/** Options of one request. */
export interface RequestOptions {
  method?: string;
  /** JSON body (serialized with `JSON.stringify`). */
  body?: unknown;
  /** No bearer token and no refresh-on-401 (sign-in, sign-up, public endpoints). */
  anonymous?: boolean;
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

/** Events of {@link ApiClient}. */
export type ApiClientEvents = {
  /** The token changed (sign-in, refresh, sign-out). */
  token: [signedIn: boolean];
  /** The session ended from the server side (a refresh was rejected). */
  signedOut: [];
};

/**
 * See the module comment.
 *
 * @example
 * ```ts
 * const api = new ApiClient({ baseUrl: '/api', sessionHintKey: 'mygame.signedIn' });
 * await api.signIn('/auth/login', { username, password });      // stores the access token
 * const me = await api.get<Me>('/me');                            // refreshes on 401 by itself
 * api.on('signedOut', () => showLogin());
 * const socketUrl = ticketUrl({ base: wsUrl, fetchTicket: () => api.connectionTicket('/game/ticket') });
 * ```
 */
export class ApiClient {
  private tokenValue: string | null = null;
  private refreshing: Promise<boolean> | null = null;
  private readonly events = new Emitter<ApiClientEvents>();
  private readonly o: ApiClientOptions;
  private readonly fetchFn: FetchLike;

  /** Creates the client (no request is made). */
  constructor(options: ApiClientOptions = {}) {
    this.o = options;
    this.fetchFn = options.fetch ?? ((input, init) => fetch(input, init));
  }

  /** True while an access token is held. */
  get signedIn(): boolean {
    return this.tokenValue !== null;
  }

  /** The current access token (for headers of your own requests), or null. */
  get token(): string | null {
    return this.tokenValue;
  }

  /** Subscribes to {@link ApiClientEvents}. */
  on<K extends keyof ApiClientEvents>(event: K, listener: (...args: ApiClientEvents[K]) => void): () => void {
    return this.events.on(event, listener);
  }

  /** Sets (or clears with null) the access token, e.g. from your own sign-in response. */
  setToken(token: string | null): void {
    const was = this.tokenValue !== null;
    this.tokenValue = token;
    this.setHint(token !== null);
    if (was !== (token !== null) || token !== null) this.events.emit('token', token !== null);
  }

  /**
   * Posts credentials anonymously to a sign-in / sign-up endpoint, stores the access token from the
   * response (see `readToken`) and returns the whole body.
   */
  async signIn<T = unknown>(path: string, body: unknown): Promise<T> {
    const r = await this.request<T>(path, { method: 'POST', body, anonymous: true });
    const token = this.readToken(r);
    if (!token) throw new ApiError(200, 'bad_response', 'The server sent no access token.');
    this.setToken(token);
    return r;
  }

  /** Posts to a sign-out endpoint (anonymous; the server clears the cookie) and drops the token locally either way. */
  async signOut(path = '/auth/logout'): Promise<void> {
    try {
      await this.request<void>(path, { method: 'POST', anonymous: true });
    } finally {
      this.tokenValue = null;
      this.setHint(false);
      this.events.emit('token', false);
    }
  }

  /**
   * Exchanges the refresh cookie for a new access token. Resolves true on success, false when the
   * session is gone (also emits `signedOut` if one was held) or a transient failure kept the old
   * token; rejects with an offline {@link ApiError} when the server is unreachable. Concurrent
   * callers share one request; tabs are serialized by a Web Lock.
   */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.doRefresh().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  /**
   * True when this browser signed in before (the refresh cookie itself is unreadable). Use it on
   * page load to skip a refresh for guests. True when no `sessionHintKey` is configured or
   * storage is blocked (then always try).
   */
  hasSessionHint(): boolean {
    if (!this.o.sessionHintKey) return true;
    try {
      const s = this.o.storage === undefined ? browserStorage() : this.o.storage;
      return s ? s.getItem(this.o.sessionHintKey) === '1' : true;
    } catch {
      return true;
    }
  }

  /**
   * Fetches a one-time connection ticket (Altruist `ConnectionTicket`, POST to your ticket
   * endpoint) for `ticketUrl` of `@altruist/net`. Accepts `{ ticket, expiresInSeconds }` and the
   * `expiresIn` spelling.
   */
  async connectionTicket(path: string): Promise<{ ticket: string; expiresInSeconds: number }> {
    const r = await this.request<{ ticket?: unknown; expiresInSeconds?: unknown; expiresIn?: unknown }>(path, { method: 'POST' });
    if (!r || typeof r.ticket !== 'string') throw new ApiError(200, 'bad_response', 'The server sent no ticket.');
    const exp = typeof r.expiresInSeconds === 'number' ? r.expiresInSeconds : typeof r.expiresIn === 'number' ? r.expiresIn : 0;
    return { ticket: r.ticket, expiresInSeconds: exp };
  }

  /** GET `path`. */
  get<T>(path: string, options: Omit<RequestOptions, 'method' | 'body'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'GET' });
  }

  /** POST `body` to `path`. */
  post<T>(path: string, body?: unknown, options: Omit<RequestOptions, 'method' | 'body'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'POST', body });
  }

  /** PUT `body` to `path`. */
  put<T>(path: string, body?: unknown, options: Omit<RequestOptions, 'method' | 'body'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'PUT', body });
  }

  /** DELETE `path`. */
  delete<T>(path: string, options: Omit<RequestOptions, 'method' | 'body'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'DELETE' });
  }

  /**
   * Sends a request and parses the JSON answer (`undefined` for 204 / empty bodies). Throws
   * {@link ApiError}: status 0 when unreachable, the server's error otherwise, `bad_response` for
   * a 2xx body that is not JSON. Authenticated requests refresh and retry once on 401.
   */
  async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    return this.send<T>(path, options, false);
  }

  private async send<T>(path: string, opts: RequestOptions, retried: boolean): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json', ...this.o.headers, ...opts.headers };
    const csrf = this.o.csrf === undefined ? { header: 'X-Requested-With', value: '1' } : this.o.csrf;
    if (csrf) headers[csrf.header] = csrf.value;
    if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
    const token = this.tokenValue;
    if (!opts.anonymous && token) headers.Authorization = `Bearer ${token}`;
    let res: Response;
    try {
      res = await this.fetchFn(`${this.o.baseUrl ?? '/api'}${path}`, {
        method: opts.method ?? 'GET',
        headers,
        body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
        credentials: 'include',
        cache: 'no-store',
        signal: opts.signal,
      });
    } catch (err) {
      if ((err as { name?: string } | null)?.name === 'AbortError') throw err;
      throw new ApiError(0, 'offline', { ...DEFAULT_ERROR_MESSAGES, ...this.o.errors?.messages }[0]!);
    }
    if (res.status === 401 && !opts.anonymous && !retried) {
      // Another call may have refreshed while this one was in flight: retry with the new token.
      if ((this.tokenValue !== null && this.tokenValue !== token) || (await this.refresh())) return this.send<T>(path, opts, true);
    }
    if (!res.ok) throw await toApiError(res, this.o.errors);
    if (res.status === 204) return undefined as T;
    const text = await res.text();
    if (!text) return undefined as T;
    try {
      return JSON.parse(text) as T;
    } catch {
      throw new ApiError(res.status, 'bad_response', 'The server sent an unexpected response.');
    }
  }

  private readToken(body: unknown): string | null {
    const read = this.o.readToken ?? ((b: unknown) => (b as { accessToken?: unknown } | null)?.accessToken);
    const t = read(body);
    return typeof t === 'string' && t ? t : null;
  }

  private async doRefresh(): Promise<boolean> {
    try {
      const call = () => this.request<unknown>(this.o.refreshPath ?? '/auth/refresh', { method: 'POST', anonymous: true });
      const nav = (globalThis as { navigator?: { locks?: LockManagerLike } }).navigator;
      const locks = this.o.locks === undefined ? nav?.locks : this.o.locks;
      // Inside the lock the browser already holds the cookie a previous tab rotated to.
      const body = locks ? await locks.request(this.o.lockName ?? 'altruist-auth-refresh', call) : await call();
      const token = this.readToken(body);
      if (!token) return false;
      this.setToken(token);
      return true;
    } catch (err) {
      // Only a definite rejection ends the session; a network blip or a 5xx keeps the current token.
      if (err instanceof ApiError && err.status !== 0 && err.status < 500) {
        const was = this.tokenValue !== null;
        this.tokenValue = null;
        this.setHint(false);
        this.events.emit('token', false);
        if (was) this.events.emit('signedOut');
      }
      if (err instanceof ApiError && err.offline) throw err;
      return false;
    }
  }

  private setHint(on: boolean): void {
    if (!this.o.sessionHintKey) return;
    try {
      const s = this.o.storage === undefined ? browserStorage() : this.o.storage;
      if (on) s?.setItem(this.o.sessionHintKey, '1');
      else s?.removeItem(this.o.sessionHintKey);
    } catch {
      /* storage blocked: the refresh is simply always tried */
    }
  }
}
