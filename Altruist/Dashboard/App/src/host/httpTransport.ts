import { splitNdjson } from './ndjson';
import { ApiError, type ApiRequest, type StateStore, type Transport } from './transport';

async function failure(response: Response): Promise<ApiError> {
  const text = await response.text().catch(() => '');
  let message = text;
  try {
    const body = JSON.parse(text) as { message?: string; error?: string; title?: string };
    message = body.message ?? body.error ?? body.title ?? text;
  } catch {
    // Not JSON: the text itself is the message.
  }
  return new ApiError(response.status, message || `${response.status} ${response.statusText}`);
}

/** Local storage under an altruist.dashboard. prefix; unavailable storage (private mode) just keeps nothing. */
export const localStore: StateStore = {
  async get<T>(key: string) {
    try {
      const text = localStorage.getItem(`altruist.dashboard.${key}`);
      return text === null ? undefined : (JSON.parse(text) as T);
    } catch {
      return undefined;
    }
  },
  async set(key: string, value: unknown) {
    try {
      localStorage.setItem(`altruist.dashboard.${key}`, JSON.stringify(value));
    } catch {
      // Storage full or blocked: the state lives for this page only.
    }
  },
};

/**
 * The browser host: the page is served by the server at /altruist/dashboard, so the API is on the same origin and the
 * dashboard session cookie (from ?dashboard_token=) authenticates every call.
 */
export function createHttpTransport(origin: string = location.origin): Transport {
  const url = (path: string) => origin + path;
  return {
    host: 'browser',
    serverLabel: origin,
    store: localStore,
    async json<T>(request: ApiRequest): Promise<T> {
      let response: Response;
      try {
        response = await fetch(url(request.path), {
          method: request.method,
          credentials: 'same-origin',
          cache: 'no-store',
          headers: request.body === undefined ? undefined : { 'Content-Type': 'application/json' },
          body: request.body === undefined ? undefined : JSON.stringify(request.body),
        });
      } catch (error) {
        throw new ApiError(0, `The server is not reachable (${(error as Error).message}).`);
      }
      if (!response.ok) throw await failure(response);
      const text = await response.text();
      return (text ? JSON.parse(text) : undefined) as T;
    },
    async lines<T>(path: string, onItem: (item: T) => boolean | void, signal?: AbortSignal): Promise<void> {
      let response: Response;
      try {
        response = await fetch(url(path), { credentials: 'same-origin', cache: 'no-store', signal });
      } catch (error) {
        if (signal?.aborted) return;
        throw new ApiError(0, `The server is not reachable (${(error as Error).message}).`);
      }
      if (!response.ok) throw await failure(response);
      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      let rest = '';
      for (;;) {
        let chunk: ReadableStreamReadResult<Uint8Array>;
        try {
          chunk = await reader.read();
        } catch (error) {
          if (signal?.aborted) return;
          throw error;
        }
        const { value, done } = chunk;
        const split = splitNdjson(rest, done ? decoder.decode() + '\n' : decoder.decode(value, { stream: true }));
        rest = split.rest;
        for (const item of split.values) {
          if (onItem(item as T) === false) {
            await reader.cancel();
            return;
          }
        }
        if (done) return;
      }
    },
    socket(onMessage, onState) {
      const ws = new WebSocket(origin.replace(/^http/, 'ws') + '/ws/dashboard');
      ws.onopen = () => onState(true);
      ws.onclose = () => onState(false);
      ws.onmessage = (event) => onMessage(JSON.parse(String(event.data)));
      return {
        send: (message: unknown) => ws.readyState === WebSocket.OPEN && ws.send(JSON.stringify(message)),
        close: () => ws.close(),
      };
    },
  };
}
