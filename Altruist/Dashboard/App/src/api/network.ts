import type { Transport } from '@/host/transport';

export interface NetworkEvent {
  readonly id: number;
  readonly timestampUtc: string;
  readonly kind: 'http' | 'packet' | string;
  readonly direction: 'inbound' | 'outbound' | string;
  readonly transport: string;
  readonly method?: string | null;
  readonly path?: string | null;
  readonly statusCode?: number | null;
  readonly route?: string | null;
  readonly portal?: string | null;
  readonly gate?: string | null;
  readonly event?: string | null;
  readonly packetType?: string | null;
  readonly connectionId?: string | null;
  readonly clientId?: string | null;
  readonly roomId?: string | null;
  readonly durationMs?: number | null;
  readonly decodeDurationMs?: number | null;
  readonly handlerDurationMs?: number | null;
  readonly encodeDurationMs?: number | null;
  readonly sendDurationMs?: number | null;
  readonly payloadBytes: number;
  readonly payloadTruncated: boolean;
  readonly payloadPreview?: string | null;
  readonly rawPayload?: string | null;
  readonly error?: string | null;
}

export interface NetworkEvents {
  readonly enabled: boolean;
  readonly retentionMinutes: number;
  readonly maxEvents: number;
  readonly maxPayloadBytes: number;
  readonly events: readonly NetworkEvent[];
}

export interface TimingSummary {
  readonly name: string;
  readonly count: number;
  readonly errorCount: number;
  readonly errorRate: number;
  readonly p50Ms: number;
  readonly p95Ms: number;
  readonly p99Ms: number;
  readonly maxMs: number;
  readonly byName: readonly TimingSummary[];
}

export interface Performance {
  readonly enabled: boolean;
  readonly http: TimingSummary;
  readonly gates: TimingSummary;
  readonly packetDecode: TimingSummary;
  readonly packetEncode: TimingSummary;
  readonly transportSend: TimingSummary;
  readonly slowRequests: readonly NetworkEvent[];
  readonly slowGates: readonly NetworkEvent[];
}

/** What an event is about: "GET /api/x" for HTTP, "route :: gate" for packets. */
export function targetOf(e: NetworkEvent): string {
  return e.kind === 'http'
    ? `${e.method ?? ''} ${e.path ?? e.route ?? ''}`.trim()
    : [e.route, e.gate ?? e.event ?? e.packetType].filter(Boolean).join(' :: ');
}

/** The longest of the event's recorded durations, for sorting. */
export function durationOf(e: NetworkEvent): number | null {
  return e.durationMs ?? e.handlerDurationMs ?? e.sendDurationMs ?? e.decodeDurationMs ?? null;
}

/** Newer events appended to `older`, the oldest dropped beyond `max`. Pure. */
export function mergeEvents(older: readonly NetworkEvent[], newer: readonly NetworkEvent[], max: number): NetworkEvent[] {
  const seen = new Set(older.map((e) => e.id));
  const merged = [...older, ...newer.filter((e) => !seen.has(e.id))].sort((a, b) => a.id - b.id);
  return merged.slice(Math.max(0, merged.length - max));
}

export const networkApi = (t: Transport) => ({
  events: (sinceId?: number, take = 500) => {
    const q = new URLSearchParams({ take: String(take) });
    if (sinceId) q.set('sinceId', String(sinceId));
    return t.json<NetworkEvents>({ method: 'GET', path: `/dashboard/v1/network/events?${q}` });
  },
  performance: () => t.json<Performance>({ method: 'GET', path: '/dashboard/v1/performance' }),
});
