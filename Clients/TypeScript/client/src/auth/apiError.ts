/**
 * Typed HTTP API failures: the client side of Altruist's `simple` error format
 * (`altruist:server:http:errors:format: simple`, C# `HttpApiException` / `HttpError`): a failed
 * response carries the status, a body `{ error, message, field? }` and, for 429 / 503, a
 * `Retry-After` header. {@link toApiError} turns any response into an {@link ApiError}; network
 * failures become status 0 (`offline`).
 *
 * Use `err.code` (snake_case, e.g. `rate_limited`, `invalid_request`, or your own codes) for logic
 * and `err.message` for display: 4xx messages come from the server and are user-facing by
 * contract; 5xx and body-less failures get a generic text from {@link DEFAULT_ERROR_MESSAGES}
 * (override per status with the `messages` option).
 */

/** Error codes Altruist itself answers with (C# `HttpErrorCodes`) plus the client-side ones. */
export const HttpErrorCodes = {
  InvalidRequest: 'invalid_request',
  Unauthorized: 'unauthorized',
  Forbidden: 'forbidden',
  RateLimited: 'rate_limited',
  PayloadTooLarge: 'payload_too_large',
  Internal: 'internal',
  /** Client-side: the server could not be reached (status 0). */
  Offline: 'offline',
  /** Client-side: a 2xx response whose body was not valid JSON. */
  BadResponse: 'bad_response',
} as const;

/** A failed API call. `status` 0 means the server could not be reached. */
export class ApiError extends Error {
  /** HTTP status (0 = offline). */
  readonly status: number;
  /** Machine code: the body's `error`, else `http_{status}` (or a {@link HttpErrorCodes} client code). */
  readonly code: string;
  /** The request field the error is about (camelCase), when the server named one. */
  readonly field: string | undefined;
  /** Seconds from `Retry-After`, when present. */
  readonly retryAfter: number | undefined;

  /** Creates the error. */
  constructor(status: number, code: string, message: string, field?: string, retryAfter?: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.field = field;
    this.retryAfter = retryAfter;
  }

  /** The server could not be reached at all (network down, server not running, CORS failure). */
  get offline(): boolean {
    return this.status === 0;
  }

  /** The session is not (or no longer) valid: 401. */
  get unauthorized(): boolean {
    return this.status === 401;
  }
}

/** Generic texts by status (0 = offline), used when the server sends no usable message. */
export const DEFAULT_ERROR_MESSAGES: Readonly<Record<number, string>> = {
  0: 'Cannot reach the server. Check your connection and try again.',
  400: 'That request was not accepted.',
  401: 'Your session has expired. Please sign in again.',
  403: 'You are not allowed to do that.',
  404: 'Not found.',
  409: 'That conflicts with the current state. Refresh and try again.',
  413: 'That request is too large.',
  429: 'Too many attempts. Please wait a moment.',
};

/** Text for 5xx responses without a usable message. */
export const SERVER_ERROR_MESSAGE = 'The server had a problem. Please try again shortly.';

/** "12 s" / "3 min" for waits. */
export function formatWait(seconds: number): string {
  if (seconds < 60) return `${Math.ceil(seconds)} s`;
  return `${Math.ceil(seconds / 60)} min`;
}

/**
 * Seconds from a `Retry-After` value: delta-seconds or an HTTP date (relative to `nowMs`).
 * Undefined when absent, invalid or not in the future.
 */
export function parseRetryAfter(value: string | null | undefined, nowMs = Date.now()): number | undefined {
  if (!value) return undefined;
  const n = Number(value);
  if (Number.isFinite(n)) return n > 0 ? n : undefined;
  const at = Date.parse(value);
  if (!Number.isFinite(at)) return undefined;
  const s = (at - nowMs) / 1000;
  return s > 0 ? Math.ceil(s) : undefined;
}

/** Options of {@link toApiError}. */
export interface ToApiErrorOptions {
  /** Per-status texts merged over {@link DEFAULT_ERROR_MESSAGES}. */
  messages?: Readonly<Record<number, string>>;
  /** Longest server message kept (default 300 characters). */
  maxMessageLength?: number;
  /** Append "— try again in 12 s" to 429 messages that contain no number (default true). */
  appendRetryHint?: boolean;
}

/** The parts of a `Response` {@link toApiError} reads. */
export interface ResponseLike {
  status: number;
  headers: { get(name: string): string | null };
  text(): Promise<string>;
}

/** Turns a failed response into an {@link ApiError} (reads the body; never throws). */
export async function toApiError(res: ResponseLike, options: ToApiErrorOptions = {}): Promise<ApiError> {
  const messages = { ...DEFAULT_ERROR_MESSAGES, ...options.messages };
  let code = `http_${res.status}`;
  let message = messages[res.status] ?? (res.status >= 500 ? SERVER_ERROR_MESSAGE : 'Request failed.');
  let field: string | undefined;
  try {
    const body = JSON.parse(await res.text()) as { error?: unknown; message?: unknown; field?: unknown };
    if (body && typeof body === 'object') {
      if (typeof body.error === 'string' && body.error) code = body.error;
      // Server messages are user-facing by contract; 5xx bodies stay generic.
      if (res.status < 500 && typeof body.message === 'string' && body.message) message = body.message.slice(0, options.maxMessageLength ?? 300);
      if (typeof body.field === 'string') field = body.field;
    }
  } catch {
    /* no JSON body */
  }
  const retryAfter = parseRetryAfter(res.headers.get('Retry-After'));
  if (res.status === 429 && retryAfter && (options.appendRetryHint ?? true) && !/\d/.test(message)) {
    message = `${message.replace(/\.$/, '')} — try again in ${formatWait(retryAfter)}.`;
  }
  return new ApiError(res.status, code, message, field, retryAfter);
}

/** A human message for any thrown value (toasts, form errors). */
export function errorMessage(err: unknown, fallback = 'Something went wrong. Please try again.'): string {
  if (err instanceof ApiError) return err.message;
  if (err instanceof Error && err.message) return err.message;
  return fallback;
}
