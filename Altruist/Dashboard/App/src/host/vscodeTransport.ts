import type { FromHost, ToHost } from './bridge';
import { ApiError, type ApiRequest, type Transport } from './transport';

/** The VS Code webview API (acquireVsCodeApi), as far as the dashboard uses it. */
export interface VsCodeApi {
  postMessage(message: ToHost): void;
  getState(): unknown;
  setState(state: unknown): void;
}

interface Pending {
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: ApiError) => void;
  readonly onLine?: (item: unknown) => boolean | void;
}

/**
 * The VS Code host: the webview cannot hold the server address or the dashboard token, so every call is a message to
 * the extension host, which performs it and answers under the same id.
 */
export function createVsCodeTransport(api: VsCodeApi, onNavigate: (route: string) => void): Transport {
  let nextId = 1;
  let serverLabel = 'VS Code';
  const pending = new Map<number, Pending>();
  let socketHandlers: { onMessage: (m: unknown) => void; onState: (open: boolean) => void } | null = null;

  window.addEventListener('message', (event: MessageEvent<FromHost>) => {
    const message = event.data;
    switch (message.type) {
      case 'result':
        pending.get(message.id)?.resolve(message.value);
        pending.delete(message.id);
        return;
      case 'failure':
        pending.get(message.id)?.reject(new ApiError(message.status, message.message));
        pending.delete(message.id);
        return;
      case 'line': {
        const entry = pending.get(message.id);
        if (entry?.onLine?.(message.item) === false) {
          api.postMessage({ type: 'cancel', id: message.id });
          entry.resolve(undefined);
          pending.delete(message.id);
        }
        return;
      }
      case 'end':
        pending.get(message.id)?.resolve(undefined);
        pending.delete(message.id);
        return;
      case 'socket-message':
        socketHandlers?.onMessage(message.message);
        return;
      case 'socket-state':
        socketHandlers?.onState(message.open);
        return;
      case 'navigate':
        onNavigate(message.route);
        return;
      case 'server':
        serverLabel = message.label;
        return;
    }
  });

  function call(message: (id: number) => ToHost, onLine?: (item: unknown) => boolean | void): Promise<unknown> {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject, onLine });
      api.postMessage(message(id));
    });
  }

  api.postMessage({ type: 'ready' });
  return {
    host: 'vscode',
    get serverLabel() {
      return serverLabel;
    },
    store: {
      get: <T>(key: string) => call((id) => ({ type: 'state-get', id, key })) as Promise<T | undefined>,
      set: (key: string, value: unknown) => call((id) => ({ type: 'state-set', id, key, value })).then(() => undefined),
    },
    json: <T>(request: ApiRequest) => call((id) => ({ type: 'json', id, request })) as Promise<T>,
    lines: <T>(path: string, onItem: (item: T) => boolean | void, signal?: AbortSignal) => {
      const id = nextId++;
      return new Promise<void>((resolve, reject) => {
        const stop = () => {
          if (!pending.has(id)) return;
          pending.delete(id);
          api.postMessage({ type: 'cancel', id });
          resolve();
        };
        pending.set(id, { resolve: () => resolve(), reject, onLine: onItem as (item: unknown) => boolean | void });
        signal?.addEventListener('abort', stop, { once: true });
        api.postMessage({ type: 'lines', id, path });
      });
    },
    socket(onMessage, onState) {
      socketHandlers = { onMessage, onState };
      api.postMessage({ type: 'socket-open' });
      return {
        send: (message: unknown) => api.postMessage({ type: 'socket-send', message }),
        close: () => {
          socketHandlers = null;
          api.postMessage({ type: 'socket-close' });
        },
      };
    },
  };
}
