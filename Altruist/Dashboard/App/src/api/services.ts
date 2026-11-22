import type { Transport } from '@/host/transport';

export interface AIAgent {
  readonly instanceId: string;
  readonly behavior: string;
  readonly state: string;
  readonly timeInState: number;
  readonly hibernated: boolean;
}

export interface GameplayReport {
  readonly behaviors: readonly { name: string; type: string }[];
  readonly agents: { tick: number; takenUtc: string; agents: readonly AIAgent[] };
  readonly zones: readonly { name: string; active: boolean; players: number; spawnPoints: number; spawnCapacity: number; spawned: number }[];
}

export interface PairingReport {
  readonly enabled: boolean;
  readonly options: { maxCompanions: number; codeLength: number; maxSessions: number; maxPayloadBytes: number } | null;
  readonly sessions: readonly { code: string; hostClientId: string; createdUtc: string; companions: readonly { slot: number; clientId: string }[] }[];
}

export interface AutosaveService {
  readonly type: string;
  readonly dirty: number;
  readonly hasVault: boolean;
  readonly batchSize: number;
  readonly walPath: string | null;
  readonly walBuffered: number;
  readonly flushes: number;
  readonly saved: number;
  readonly lastFlushUtc: string | null;
  readonly failures: number;
  readonly lastError: string | null;
  readonly lastErrorUtc: string | null;
}

/** States and how many agents are in each, most common first. Pure. */
export function stateCounts(agents: readonly AIAgent[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const a of agents) counts.set(`${a.behavior} · ${a.state}`, (counts.get(`${a.behavior} · ${a.state}`) ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

export const servicesApi = (t: Transport) => ({
  gameplay: () => t.json<GameplayReport>({ method: 'GET', path: '/dashboard/v1/gameplay' }),
  pairing: () => t.json<PairingReport>({ method: 'GET', path: '/dashboard/v1/pairing' }),
  autosave: () => t.json<AutosaveService[]>({ method: 'GET', path: '/dashboard/v1/autosave' }),
  flushAutosave: () => t.json<AutosaveService[]>({ method: 'POST', path: '/dashboard/v1/autosave/flush' }),
});
