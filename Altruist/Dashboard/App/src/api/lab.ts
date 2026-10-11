import type { Transport } from '@/host/transport';

export interface LabAction {
  readonly id: string;
  readonly kind: 'http' | 'gate' | string;
  readonly method: string;
  readonly path?: string | null;
  readonly event?: string | null;
  readonly name: string;
  readonly handler: string;
  readonly payloadType?: string | null;
  readonly sampleJson: string;
  readonly requiresAuth: boolean;
  readonly shield?: string | null;
}

export interface LabResult {
  readonly success: boolean;
  readonly statusCode?: number | null;
  readonly message?: string | null;
  readonly responseBody?: string | null;
}

/** A saved call: which action, with what body and client id. */
export interface LabCall {
  readonly id: string;
  readonly actionId: string;
  readonly bodyJson: string;
  readonly clientId: string;
}

/** A named list of saved calls, run in order. */
export interface LabCollection {
  readonly id: string;
  readonly name: string;
  readonly calls: readonly LabCall[];
}

export const LAB_STORE_KEY = 'lab.collections.v2';

const METHOD_RANK: Readonly<Record<string, number>> = { GET: 0, POST: 1, PUT: 2, PATCH: 3, DELETE: 4 };

export function isAuthAction(a: LabAction): boolean {
  return /auth|login|token|identity|signup|register/i.test(`${a.path ?? ''} ${a.name} ${a.handler}`);
}

/** What a row shows next to its method badge: the path, or "route :: event" for a gate. */
export function targetOf(a: LabAction): string {
  return a.kind === 'gate' ? `${a.path ?? ''} :: ${a.event ?? a.name}` : (a.path ?? a.name);
}

export function describeAction(a: LabAction): string {
  return a.kind === 'gate' ? `${a.path ?? ''} :: ${a.event ?? a.name}` : `${a.method || 'GET'} ${a.path ?? a.name}`;
}

/** Sections in display order: Auth first, then HTTP by method, then portal gates. Pure. */
export function labSections(actions: readonly LabAction[]): { title: string; actions: LabAction[] }[] {
  const byTarget = (a: LabAction, b: LabAction) => describeAction(a).localeCompare(describeAction(b), undefined, { sensitivity: 'base' });
  const http = actions.filter((a) => a.kind === 'http');
  const auth = http.filter(isAuthAction).sort(byTarget);
  const methods = new Map<string, LabAction[]>();
  for (const a of http.filter((x) => !isAuthAction(x))) {
    const m = (a.method || 'GET').toUpperCase();
    methods.set(m, [...(methods.get(m) ?? []), a]);
  }
  return [
    ...(auth.length ? [{ title: 'Auth', actions: auth }] : []),
    ...[...methods.entries()].sort(([a], [b]) => (METHOD_RANK[a] ?? 99) - (METHOD_RANK[b] ?? 99)).map(([title, list]) => ({ title, actions: list.sort(byTarget) })),
    ...(actions.some((a) => a.kind === 'gate') ? [{ title: 'Portal gates', actions: actions.filter((a) => a.kind === 'gate').sort(byTarget) }] : []),
  ];
}

export const labApi = (t: Transport) => ({
  actions: () => t.json<{ actions: LabAction[] }>({ method: 'GET', path: '/dashboard/v1/lab/actions' }).then((r) => r.actions),
  invoke: (action: LabAction, bodyJson: string, clientId: string) =>
    t.json<LabResult>({
      method: 'POST',
      path: '/dashboard/v1/lab/invoke',
      body: { kind: action.kind, method: action.method, path: action.path, event: action.event, clientId: clientId || null, bodyJson },
    }),
});
