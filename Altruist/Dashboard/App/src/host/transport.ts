/** An HTTP call to the dashboard API (paths start with /dashboard/v1). */
export interface ApiRequest {
  readonly method: 'GET' | 'POST' | 'PUT' | 'DELETE';
  readonly path: string;
  readonly body?: unknown;
}

/** A failed API call: the HTTP status (0 when the server was not reached) and the server's message. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** A live subscription; `close` ends it. */
export interface Subscription {
  close(): void;
}

/**
 * How the dashboard reaches the server. In a browser it is plain fetch/WebSocket on the page's origin; in the VS Code
 * webview every call goes through the extension host, which knows the server address and the dashboard token.
 */
export interface Transport {
  /** Which host the app runs in. */
  readonly host: 'browser' | 'vscode';
  /** The server's base URL, for display. */
  readonly serverLabel: string;
  /** A JSON call; resolves to the parsed body (undefined for an empty body). */
  json<T>(request: ApiRequest): Promise<T>;
  /** An NDJSON stream: `onItem` gets every line's value; returning false, or aborting `signal`, stops reading. */
  lines<T>(path: string, onItem: (item: T) => boolean | void, signal?: AbortSignal): Promise<void>;
  /** The /ws/dashboard socket: every server message goes to `onMessage`; `send` writes one. */
  socket(onMessage: (message: unknown) => void, onState: (open: boolean) => void): Subscription & { send(message: unknown): void };
  /** Saved UI state (e.g. API Lab collections): the browser's local storage, or the VS Code workspace. */
  readonly store: StateStore;
}

export interface StateStore {
  get<T>(key: string): Promise<T | undefined>;
  set(key: string, value: unknown): Promise<void>;
}
