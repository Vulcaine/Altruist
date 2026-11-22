import type { Transport } from '@/host/transport';

export interface CapacityByKind {
  readonly kind: string;
  readonly units: number;
  readonly load: number;
}

export interface NodeReport {
  readonly nodeId: string;
  /** Starting | Ready | Full | Draining | Drained */
  readonly state: string;
  readonly load: number;
  readonly maxLoad: number;
  readonly utilization: number | null;
  readonly accepting: boolean;
  readonly kinds: readonly CapacityByKind[];
  readonly drainTimeoutSeconds: number;
  readonly draining: boolean;
  readonly drained: boolean;
  readonly forced: boolean;
  readonly drainStartedUtc: string | null;
  readonly participants: readonly { kind: string; isDrained: boolean }[];
}

export interface FleetNode {
  readonly nodeId: string;
  readonly region: string;
  readonly internalAddress?: string | null;
  readonly publicAddress?: string | null;
  readonly state: string | number;
  readonly load: number;
  readonly maxLoad: number;
  readonly stats: Readonly<Record<string, number>>;
  readonly seenUtc: string;
  readonly accepting: boolean;
}

export interface FleetReport {
  readonly cluster: string;
  readonly region: string;
  readonly shared: boolean;
  readonly backplane: string | null;
  readonly heartbeatSeconds: number | null;
  readonly nodeTtlSeconds: number | null;
  readonly selfId: string;
  readonly nodes: readonly FleetNode[];
}

export interface FleetClaim {
  readonly kind: string;
  readonly id: string;
  readonly nodeId: string;
  readonly nodeLive: boolean;
}

export interface ProcessReport {
  readonly processId: number;
  readonly machine: string;
  readonly runtime: string;
  readonly uptimeSeconds: number;
  readonly workingSetBytes: number;
  readonly gcHeapBytes: number;
  readonly gcTotalAllocatedBytes: number;
  readonly gen0Collections: number;
  readonly gen1Collections: number;
  readonly gen2Collections: number;
  readonly gcPauseTimePercent: number;
  readonly cpuPercent: number;
  readonly processors: number;
  readonly threads: number;
  readonly threadPoolThreads: number;
  readonly threadPoolQueue: number;
  readonly lockContentions: number;
}

export interface ServerReport {
  readonly node: NodeReport;
  readonly fleet: FleetReport | null;
  readonly claims: readonly FleetClaim[];
  readonly process: ProcessReport;
  readonly tickets: { queryParam: string; lifetimeSeconds: number; maxPerPrincipal: number; sessionLifetimeSeconds: number } | null;
  readonly httpRateLimits: readonly { name: string; permit: number; windowSeconds: number; partition: string; path?: string | null }[];
  readonly transportRateLimit: {
    enabled: boolean;
    maxPayloadBytes: number;
    strikesToDisconnect: number;
    defaultBucket: string;
    buckets: Readonly<Record<string, { capacity: number; perSecond: number; gates: readonly string[] }>>;
  } | null;
}

const STATES = ['Starting', 'Ready', 'Full', 'Draining', 'Drained'];

/** The server sends a fleet node's state as a name or as the enum's number. */
export function stateName(state: string | number): string {
  return typeof state === 'number' ? (STATES[state] ?? String(state)) : state;
}

/** Badge tone of a server state. */
export const STATE_TONE: Readonly<Record<string, 'ok' | 'warn' | 'danger' | 'muted'>> = { Ready: 'ok', Full: 'warn', Starting: 'muted', Draining: 'warn', Drained: 'danger' };

export const serverApi = (t: Transport) => ({
  report: () => t.json<ServerReport>({ method: 'GET', path: '/dashboard/v1/server' }),
  drain: (timeoutSeconds?: number) =>
    t.json<NodeReport>({ method: 'POST', path: `/dashboard/v1/server/drain${timeoutSeconds ? `?timeoutSeconds=${timeoutSeconds}` : ''}` }),
});
