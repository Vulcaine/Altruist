import type { Transport } from '@/host/transport';

/** A socket-layer room (the transport's grouping of connections; game rooms are under /rooms). */
export interface SocketRoom {
  readonly roomId: string;
  readonly connectionCount: number;
  readonly connections: readonly { readonly connectionId: string }[];
}

export interface Connection {
  readonly connectionId: string;
  readonly roomId?: string | null;
  readonly ipAddress?: string | null;
}

const BASE = '/dashboard/v1/sessions';
const enc = encodeURIComponent;

export const connectionsApi = (t: Transport) => ({
  rooms: () => t.json<SocketRoom[]>({ method: 'GET', path: BASE }),
  connections: () => t.json<Connection[]>({ method: 'GET', path: `${BASE}/connections` }),
  disconnect: (connectionId: string) => t.json<void>({ method: 'DELETE', path: `${BASE}/connections/${enc(connectionId)}` }),
  deleteRoom: (roomId: string) => t.json<void>({ method: 'DELETE', path: `${BASE}/rooms/${enc(roomId)}` }),
});
