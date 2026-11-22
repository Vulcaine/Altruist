import type { Transport } from '@/host/transport';

export interface CacheInfo {
  readonly provider: string;
  readonly isConnected: boolean;
  readonly inMemoryEntryCount: number;
}

export interface CacheEntry {
  readonly type: string;
  readonly typeShortName: string;
  readonly groupId: string;
  readonly key: string;
  readonly value: unknown;
  readonly preview?: string | null;
  readonly source: string;
}

export type CacheKey = Pick<CacheEntry, 'type' | 'groupId' | 'key'>;

const BASE = '/dashboard/v1/cache';

export const cacheApi = (t: Transport) => ({
  info: () => t.json<CacheInfo>({ method: 'GET', path: `${BASE}/info` }),
  entries: async () => {
    const entries: CacheEntry[] = [];
    await t.lines<CacheEntry>(`${BASE}/entries/stream`, (e) => {
      entries.push(e);
    });
    return entries;
  },
  save: (key: CacheKey, value: unknown) =>
    t.json<void>({ method: 'PUT', path: `${BASE}/entry`, body: { type: key.type, groupId: key.groupId ?? '', key: key.key, value } }),
  remove: (key: CacheKey) => {
    const query = new URLSearchParams({ type: key.type, groupId: key.groupId ?? '', key: key.key });
    return t.json<void>({ method: 'DELETE', path: `${BASE}/entry?${query}` });
  },
});
