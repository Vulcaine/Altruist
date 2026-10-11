import type { Transport } from '@/host/transport';

export interface SeatReport {
  readonly id: number;
  readonly team: number;
  readonly owner: string | null;
  readonly bot: string | null;
  readonly openSeconds: number | null;
  readonly pending: boolean;
}

export interface InputReport {
  readonly depth: number;
  readonly targetDepth: number;
  readonly jitter: number;
  readonly speculating: number;
  readonly applied: number;
  readonly starved: number;
  readonly stale: number;
  readonly dropped: number;
}

export interface ParticipantReport {
  readonly principalId: string;
  readonly clientId: string | null;
  readonly team: number;
  readonly seat: number | null;
  readonly connected: boolean;
  readonly disconnectedSeconds: number | null;
  readonly leftEarly: boolean;
  readonly abandoned: boolean;
  readonly input: InputReport;
}

export interface RoomReport {
  readonly id: string;
  readonly mode: string;
  readonly playlist: string | null;
  readonly lobbyCode: string | null;
  readonly startedAtUtc: string;
  readonly load: number;
  readonly ended: boolean;
  readonly finished: boolean;
  readonly endedSeconds: number;
  readonly seats: readonly SeatReport[];
  readonly participants: readonly ParticipantReport[];
}

export interface ModuleReport {
  readonly name: string;
  readonly facts: readonly { key: string; value: string }[];
  readonly tables: readonly { title: string; columns: readonly string[]; rows: readonly (readonly string[])[] }[];
}

export interface RoomHostReport {
  readonly game: string;
  readonly fixedHz: number;
  readonly step: number;
  readonly sessions: number;
  readonly draining: boolean;
  readonly rooms: readonly RoomReport[];
  readonly modules: readonly ModuleReport[];
}

export interface RoomHostEntry {
  readonly index: number;
  readonly game: string;
  readonly report: RoomHostReport | null;
  readonly error: string | null;
}

/** A room's status for display. */
export function roomStatus(r: RoomReport): 'running' | 'ended' | 'finished' {
  return r.finished ? 'finished' : r.ended ? 'ended' : 'running';
}

export const roomsApi = (t: Transport) => ({
  hosts: () => t.json<RoomHostEntry[]>({ method: 'GET', path: '/dashboard/v1/rooms' }),
  close: (host: number, roomId: string) => t.json<void>({ method: 'POST', path: `/dashboard/v1/rooms/${host}/${encodeURIComponent(roomId)}/close` }),
});
