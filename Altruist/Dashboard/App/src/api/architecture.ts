import type { Transport } from '@/host/transport';
import type { FleetNode } from './server';

export interface ArchitectureListener {
  /** http | websocket | tcp | udp */
  readonly transport: string;
  readonly address: string;
  readonly codec: string | null;
  readonly connections: number | null;
}

export interface ArchitecturePortal {
  readonly route: string;
  readonly type: string;
  readonly connections: number;
}

export interface ArchitectureStore {
  readonly id: string;
  readonly target: string;
  readonly provider: string;
  readonly name: string;
  /** database | cache | backplane */
  readonly use: string;
  readonly endpoints: readonly string[];
  readonly database: string | null;
  readonly role: string | null;
  readonly local: boolean;
  readonly vaults: readonly string[];
}

export interface ArchitectureCard {
  readonly nodeId: string;
  readonly cluster: string | null;
  readonly region: string;
  readonly state: string;
  readonly load: number;
  readonly maxLoad: number;
  readonly environment: string;
  readonly machine: string;
  readonly processId: number;
  readonly internalAddress: string | null;
  readonly publicAddress: string | null;
  readonly startedUtc: string;
  readonly publishedUtc: string;
  readonly listeners: readonly ArchitectureListener[];
  readonly portals: readonly ArchitecturePortal[];
  readonly stores: readonly ArchitectureStore[];
}

export interface ArchitectureReport {
  readonly self: string;
  readonly cluster: string | null;
  readonly backplaneKind: string | null;
  readonly shared: boolean;
  readonly nodes: readonly FleetNode[];
  readonly cards: readonly ArchitectureCard[];
}

export const architectureApi = (t: Transport) => ({
  report: () => t.json<ArchitectureReport>({ method: 'GET', path: '/dashboard/v1/architecture' }),
});

/** A server: of this fleet (heartbeat entry, card, or both) or of another cluster on the backplane (card). */
export interface ServerBox {
  readonly id: string;
  readonly self: boolean;
  readonly cluster: string | null;
  /** Of another cluster: an independent fleet (a channel) sharing the backplane. */
  readonly foreign: boolean;
  /** Missing when the server runs no dashboard (only its heartbeat is known). */
  readonly card: ArchitectureCard | null;
  /** Missing for another cluster's server, and when the card outlived the server's heartbeat (it is stopping). */
  readonly node: FleetNode | null;
  /** In-memory stores of the server (never shared). */
  readonly localStores: readonly ArchitectureStore[];
}

/** One use of a shared store, by every server that uses it the same way. */
export interface StoreUse {
  readonly id: string;
  readonly name: string;
  readonly use: string;
  readonly role: string | null;
  readonly vaults: readonly string[];
  readonly servers: readonly string[];
}

/** A physical store several servers may use: a database server, a Redis. */
export interface StoreBox {
  readonly target: string;
  readonly provider: string;
  readonly endpoints: readonly string[];
  readonly database: string | null;
  readonly uses: readonly StoreUse[];
}

export interface ArchitectureEdge {
  readonly server: string;
  readonly useId: string;
  readonly use: string;
  readonly role: string | null;
}

/** The servers of one cluster. */
export interface ClusterGroup {
  readonly cluster: string | null;
  readonly own: boolean;
  readonly servers: readonly ServerBox[];
}

export interface ArchitectureGraph {
  readonly servers: readonly ServerBox[];
  /** The servers by cluster: this server's first, then the others by name. */
  readonly clusters: readonly ClusterGroup[];
  readonly stores: readonly StoreBox[];
  readonly edges: readonly ArchitectureEdge[];
}

const USE_ORDER: Readonly<Record<string, number>> = { database: 0, cache: 1, backplane: 2 };
const byOrdinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
const useRank = (use: string) => USE_ORDER[use] ?? 9;

/**
 * The fleet as boxes and edges: every server (heartbeat or card; this one first), every shared store once, however
 * many servers use it, with the uses each server makes of it.
 */
export function architectureGraph(report: ArchitectureReport): ArchitectureGraph {
  const cards = new Map(report.cards.map((c) => [c.nodeId, c]));
  const nodes = new Map(report.nodes.map((n) => [n.nodeId, n]));
  const ids = [...new Set([...cards.keys(), ...nodes.keys()])];
  const boxes: ServerBox[] = ids.map((id) => {
    const card = cards.get(id) ?? null;
    const cluster = nodes.has(id) ? report.cluster : (card?.cluster ?? null);
    return {
      id,
      self: id === report.self,
      cluster,
      foreign: cluster !== report.cluster,
      card,
      node: nodes.get(id) ?? null,
      localStores: card?.stores.filter((s) => s.local) ?? [],
    };
  });
  const clusters = [...groupBy(boxes, (b) => b.cluster ?? '')]
    .map(([, list]): ClusterGroup => ({
      cluster: list[0].cluster,
      own: !list[0].foreign,
      servers: list.sort((a, b) => (a.self ? -1 : b.self ? 1 : byOrdinal(a.id, b.id))),
    }))
    .sort((a, b) => (a.own ? -1 : b.own ? 1 : byOrdinal(a.cluster ?? '', b.cluster ?? '')));
  const servers = clusters.flatMap((c) => c.servers);

  const shared = report.cards.flatMap((c) => c.stores.filter((s) => !s.local).map((store) => ({ server: c.nodeId, store })));
  const stores = [...groupBy(shared, (s) => s.store.target)].map(([target, entries]): StoreBox => {
    const first = entries[0].store;
    const uses = [...groupBy(entries, (e) => e.store.id)].map(([id, list]): StoreUse => ({
      id,
      name: list[0].store.name,
      use: list[0].store.use,
      role: list[0].store.role,
      vaults: [...new Set(list.flatMap((e) => e.store.vaults))].sort(byOrdinal),
      servers: [...new Set(list.map((e) => e.server))].sort(byOrdinal),
    }));
    uses.sort((a, b) => useRank(a.use) - useRank(b.use) || byOrdinal(a.name, b.name));
    return { target, provider: first.provider, endpoints: first.endpoints, database: first.database, uses };
  });
  stores.sort((a, b) => useRank(a.uses[0].use) - useRank(b.uses[0].use) || byOrdinal(a.target, b.target));

  const edges = shared.map(({ server, store }) => ({ server, useId: store.id, use: store.use, role: store.role }));
  return { servers, clusters, stores, edges };
}

function groupBy<T>(items: readonly T[], key: (item: T) => string): Map<string, T[]> {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const k = key(item);
    const list = groups.get(k);
    if (list) list.push(item);
    else groups.set(k, [item]);
  }
  return groups;
}

/** Open connections of a server over all its socket transports. */
export function connectionsOf(card: ArchitectureCard): number {
  // A transport listening on several addresses reports its connections on each.
  const byTransport = new Map(card.listeners.map((l) => [l.transport, l.connections ?? 0]));
  return [...byTransport.values()].reduce((sum, n) => sum + n, 0);
}

/** A server's state and load: from its heartbeat in this fleet, else from its card. */
export function statusOf(server: ServerBox): { readonly state: string; readonly load: number; readonly maxLoad: number } | null {
  if (server.node) return { state: String(server.node.state), load: server.node.load, maxLoad: server.node.maxLoad };
  if (server.card && server.foreign) return { state: server.card.state, load: server.card.load, maxLoad: server.card.maxLoad };
  return null;
}
