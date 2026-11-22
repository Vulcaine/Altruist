import type { ApiRequest } from './transport';

/**
 * The messages between the webview and the VS Code extension host (VSCode/lib/bridge.js speaks the other side).
 * Requests carry an id; the host answers with results, stream lines and the end of a stream under the same id.
 */
export type ToHost =
  | { readonly type: 'json'; readonly id: number; readonly request: ApiRequest }
  | { readonly type: 'lines'; readonly id: number; readonly path: string }
  | { readonly type: 'cancel'; readonly id: number }
  | { readonly type: 'socket-open' }
  | { readonly type: 'socket-send'; readonly message: unknown }
  | { readonly type: 'socket-close' }
  | { readonly type: 'state-get'; readonly id: number; readonly key: string }
  | { readonly type: 'state-set'; readonly id: number; readonly key: string; readonly value: unknown }
  | { readonly type: 'ready' };

export type FromHost =
  | { readonly type: 'result'; readonly id: number; readonly value: unknown }
  | { readonly type: 'failure'; readonly id: number; readonly status: number; readonly message: string }
  | { readonly type: 'line'; readonly id: number; readonly item: unknown }
  | { readonly type: 'end'; readonly id: number }
  | { readonly type: 'socket-message'; readonly message: unknown }
  | { readonly type: 'socket-state'; readonly open: boolean }
  | { readonly type: 'navigate'; readonly route: string }
  | { readonly type: 'server'; readonly label: string };
