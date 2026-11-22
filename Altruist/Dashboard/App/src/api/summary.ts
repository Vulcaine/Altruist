import type { Transport } from '@/host/transport';

export interface ConfigEntry {
  readonly key: string;
  readonly value: string | null;
  readonly modifiable: boolean;
}

export type ServiceCategory = 'Portal' | 'Service' | 'ServiceFactory' | 'ServiceConfiguration';

export interface ServiceInfo {
  readonly name: string;
  readonly fullName: string;
  readonly assembly: string;
  readonly category: ServiceCategory | number;
  readonly lifetime?: string | null;
  readonly serviceType?: string | null;
  readonly endpoint?: string | null;
  readonly context?: string | null;
}

export interface EngineInfo {
  readonly diagnostics: boolean;
  readonly framerateHz: number;
  readonly unit: string;
  readonly throttle?: number | null;
  readonly gravity?: { x: number; y: number; z: number } | null;
}

export interface Summary {
  readonly configs: readonly ConfigEntry[];
  readonly serviceCount: number;
  readonly services: readonly ServiceInfo[];
  readonly engine?: EngineInfo | null;
  /** The dashboard areas the server serves (missing on servers before 0.9.10). */
  readonly features?: readonly string[];
}

const CATEGORIES: readonly ServiceCategory[] = ['Portal', 'Service', 'ServiceFactory', 'ServiceConfiguration'];

/** The server sends the category as a name or as the enum's number, depending on its JSON settings. */
export function categoryOf(service: ServiceInfo): ServiceCategory {
  return typeof service.category === 'number' ? (CATEGORIES[service.category] ?? 'Service') : service.category;
}

export function configValue(summary: Summary, key: string): string | null {
  return summary.configs.find((c) => c.key.toLowerCase() === key.toLowerCase())?.value ?? null;
}

export const summaryApi = (t: Transport) => ({
  get: () => t.json<Summary>({ method: 'GET', path: '/dashboard/v1/summary' }),
  updateConfig: (entries: readonly { key: string; value: string }[]) =>
    t.json<{ updated: number }>({ method: 'POST', path: '/dashboard/v1/summary/config/update-batch', body: entries }),
});
