import type { RefObject } from 'preact';
import { useLayoutEffect, useMemo, useRef, useState } from 'preact/hooks';
import {
  architectureApi,
  type ArchitectureGraph,
  architectureGraph,
  type ArchitectureReport,
  type ArchitectureStore,
  connectionsOf,
  type ServerBox,
  statusOf,
  type StoreBox,
  type StoreUse,
} from '@/api/architecture';
import { STATE_TONE, stateName } from '@/api/server';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Card, Icon, KeyValues, Stat, Stats } from '@/ui/basics';
import { formatAgo, formatNumber } from '@/ui/format';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

type Selection = { readonly kind: 'server' | 'use'; readonly id: string } | null;

interface EdgePath {
  readonly key: string;
  readonly d: string;
  readonly kind: string;
  readonly readonly: boolean;
  readonly server: string;
  readonly useId: string | null;
}

/** The fleet's servers, where clients reach them, and the databases, caches and backplane they share. */
export function ArchitectureView() {
  const api = architectureApi(useTransport());
  const [report, reload] = useResource(api.report, [], 3000);
  return (
    <Page title="Architecture" subtitle="The fleet's servers and the stores they share. Refreshes every 3 s." resource={report} reload={reload}>
      <ResourceView resource={report} reload={reload}>
        {(r) => <Architecture report={r} />}
      </ResourceView>
    </Page>
  );
}

function Architecture({ report }: { report: ArchitectureReport }) {
  const graph = useMemo(() => architectureGraph(report), [report]);
  const [selected, setSelected] = useState<Selection>(null);
  const toggle = (next: NonNullable<Selection>) => setSelected((s) => (s?.kind === next.kind && s.id === next.id ? null : next));
  const lit = useMemo(() => litBy(selected, graph), [selected, graph]);
  const diagram = useRef<HTMLDivElement>(null);
  const paths = useEdgePaths(diagram, graph);
  const sharedUses = graph.stores.flatMap((s) => s.uses);
  const connections = graph.servers.reduce((sum, s) => sum + (s.card ? connectionsOf(s.card) : 0), 0);
  const selectedUse = selected?.kind === 'use' ? sharedUses.find((u) => u.id === selected.id) : undefined;

  return (
    <>
      <Stats>
        <Stat
          label="Servers"
          value={graph.servers.length}
          hint={graph.clusters.length > 1 ? `${graph.clusters.length} clusters` : report.cluster ? `cluster ${report.cluster}` : 'no fleet'}
        />
        <Stat label="Shared stores" value={graph.stores.length} hint={`${sharedUses.length} uses`} />
        <Stat label="Connections" value={formatNumber(connections)} hint="over every server" />
        <Stat label="Backplane" value={report.backplaneKind ?? '—'} hint={report.shared ? 'shared by the fleet' : 'this server only'} />
      </Stats>
      {!report.shared && (
        <p class="muted">
          The fleet backplane is in this server's memory, so no other server can be seen. Servers sharing <span class="mono">altruist:server:fleet:redis</span>{' '}
          and a cluster name appear here together.
        </p>
      )}
      <Legend />
      <div class="arch" ref={diagram} onClick={(e) => e.target === e.currentTarget && setSelected(null)}>
        <svg class="arch-edges" aria-hidden="true">
          {paths.map((p) => (
            <path key={p.key} d={p.d} class={`arch-edge arch-edge-${p.kind}${p.readonly ? ' arch-edge-ro' : ''}${lit && !isLit(p, lit) ? ' arch-dim' : ''}`} />
          ))}
        </svg>
        <div class="arch-col arch-col-clients">
          <div class="arch-box arch-clients" data-anchor="clients">
            <Icon name="account" /> Clients
            <div class="muted">
              {graph.clusters.length > 1 ? 'pick a fleet (channel); ' : ''}
              {graph.servers.some((s) => s.card?.publicAddress) ? 'then a server' : 'then any of its servers, which relays (?node=)'}
            </div>
          </div>
        </div>
        <div class="arch-col">
          {graph.clusters.map((c) => (
            <div key={c.cluster ?? ''} class="arch-cluster">
              {(graph.clusters.length > 1 || c.cluster) && (
                <div class="row arch-cluster-head">
                  <Icon name={c.own ? 'server-environment' : 'server-process'} />
                  <span>{c.cluster ? `cluster ${c.cluster}` : 'no fleet'}</span>
                  <span class="muted">{c.own ? 'this fleet' : 'a separate fleet (channel) on the same backplane'}</span>
                </div>
              )}
              {c.servers.map((s) => (
                <ServerCard key={s.id} server={s} dim={!!lit && !lit.servers.has(s.id)} active={selected?.kind === 'server' && selected.id === s.id} onSelect={() => toggle({ kind: 'server', id: s.id })} />
              ))}
            </div>
          ))}
        </div>
        <div class="arch-col">
          {graph.stores.length === 0 ? (
            <div class="arch-box muted">No shared store: every store is in a server's memory.</div>
          ) : (
            graph.stores.map((s) => <StoreCard key={s.target} store={s} lit={lit} selected={selected} onSelect={(id) => toggle({ kind: 'use', id })} />)
          )}
        </div>
      </div>
      {selectedUse && <UseDetails use={selectedUse} />}
    </>
  );
}

function Legend() {
  const items: readonly (readonly [string, string])[] = [
    ['client', 'clients'],
    ['database', 'database'],
    ['readonly', 'read-only database'],
    ['cache', 'cache'],
    ['backplane', 'fleet backplane'],
  ];
  return (
    <div class="row arch-legend">
      {items.map(([kind, label]) => (
        <span key={kind} class="row">
          <svg width="28" height="8" aria-hidden="true">
            <path d="M0 4 H28" class={`arch-edge ${kind === 'readonly' ? 'arch-edge-database arch-edge-ro' : `arch-edge-${kind}`}`} />
          </svg>
          <span class="muted">{label}</span>
        </span>
      ))}
      <span class="muted">· Click a server or a store to follow its connections.</span>
    </div>
  );
}

function ServerCard({ server, dim, active, onSelect }: { server: ServerBox; dim: boolean; active: boolean; onSelect: () => void }) {
  const { card } = server;
  const status = statusOf(server);
  const state = status ? stateName(status.state) : 'Leaving';
  return (
    <div class={`arch-box arch-server${server.self ? ' arch-self' : ''}${dim ? ' arch-dim' : ''}${active ? ' arch-active' : ''}`} onClick={onSelect}>
      <div class="row arch-server-head" data-anchor={`server:${server.id}`}>
        <Icon name="server" />
        <strong class="mono clip grow" title={server.id}>
          {server.id}
        </strong>
        {server.self && <Badge tone="accent">this server</Badge>}
        <Badge tone={status ? (STATE_TONE[state] ?? 'muted') : 'warn'} title={status ? undefined : 'Its card is still published, its heartbeat is gone.'}>
          {state}
        </Badge>
      </div>
      {status && status.maxLoad > 0 && (
        <div class="row arch-load">
          <div class="meter grow">
            <div class="meter-fill" style={{ width: `${Math.min(100, (status.load / status.maxLoad) * 100)}%` }} />
          </div>
          <span class="muted">
            load {formatNumber(status.load, 1)} / {formatNumber(status.maxLoad)}
          </span>
        </div>
      )}
      {card ? <ServerDetails card={card} localStores={server.localStores} /> : <div class="muted">No dashboard on this server: only its heartbeat is known.</div>}
    </div>
  );
}

function ServerDetails({ card, localStores }: { card: NonNullable<ServerBox['card']>; localStores: readonly ArchitectureStore[] }) {
  return (
    <>
      <div class="arch-lines">
        {card.listeners.map((l) => (
          <div key={`${l.transport}:${l.address}`} class="row arch-line">
            <Badge tone={l.transport === 'http' ? 'muted' : 'accent'}>{l.transport}</Badge>
            <span class="mono clip grow" title={l.address}>
              {l.address}
            </span>
            {l.codec && <span class="muted">{l.codec}</span>}
            {l.connections != null && <span title="open connections">{l.connections}</span>}
          </div>
        ))}
        {card.portals.map((p) => (
          <div key={`${p.route}:${p.type}`} class="row arch-line">
            <Icon name="plug" />
            <span class="mono">{p.route}</span>
            <span class="muted clip grow">{p.type}</span>
            <span title="open connections">{p.connections}</span>
          </div>
        ))}
        {localStores.map((s) => (
          <div key={s.id} class="row arch-line">
            <Icon name="chip" />
            <span>{s.use}</span>
            <span class="muted grow">in this server's memory</span>
          </div>
        ))}
      </div>
      <div class="muted arch-foot">
        {[card.machine, `pid ${card.processId}`, card.environment, card.region, card.internalAddress && `internal ${card.internalAddress}`, `up ${formatAgo(card.startedUtc).replace(' ago', '')}`]
          .filter(Boolean)
          .join(' · ')}
      </div>
    </>
  );
}

function StoreCard({ store, lit, selected, onSelect }: { store: StoreBox; lit: Lit | null; selected: Selection; onSelect: (useId: string) => void }) {
  const dim = !!lit && !store.uses.some((u) => lit.uses.has(u.id));
  return (
    <div class={`arch-box arch-store${dim ? ' arch-dim' : ''}`}>
      <div class="row">
        <Icon name={store.provider === 'redis' ? 'layers' : 'database'} />
        <strong>{store.provider}</strong>
        <span class="mono muted clip grow" title={store.endpoints.join(', ')}>
          {store.endpoints.join(', ')}
          {store.database && `/${store.database}`}
        </span>
      </div>
      {store.uses.map((u) => (
        <div
          key={u.id}
          class={`row arch-use${lit && !lit.uses.has(u.id) ? ' arch-dim' : ''}${selected?.kind === 'use' && selected.id === u.id ? ' arch-active' : ''}`}
          data-anchor={`use:${u.id}`}
          onClick={() => onSelect(u.id)}
        >
          <span class="mono">{u.name}</span>
          <Badge tone={u.use === 'database' ? (u.role === 'readonly' ? 'warn' : 'ok') : 'muted'}>{u.use === 'database' ? u.role : u.use}</Badge>
          <span class="muted grow">{u.vaults.length > 0 && `${u.vaults.length} vault${u.vaults.length === 1 ? '' : 's'}`}</span>
          <span class="muted">
            {u.servers.length} server{u.servers.length === 1 ? '' : 's'}
          </span>
        </div>
      ))}
    </div>
  );
}

function UseDetails({ use }: { use: StoreUse }) {
  return (
    <Card title={`${use.name} (${use.use})`}>
      <KeyValues
        items={[
          ['Used by', <span class="mono">{use.servers.join(', ')}</span>],
          ...(use.role ? ([['Role', use.role]] as const) : []),
          ['Vaults', use.vaults.length === 0 ? <span class="muted">none</span> : <span class="mono">{use.vaults.join(', ')}</span>],
        ]}
      />
    </Card>
  );
}

// ------------------------------------------------------------------ selection

interface Lit {
  readonly servers: ReadonlySet<string>;
  readonly uses: ReadonlySet<string>;
}

/** What a selection lights up: a server and the stores it uses, or a store use and the servers using it. */
function litBy(selected: Selection, graph: ArchitectureGraph): Lit | null {
  if (!selected) return null;
  if (selected.kind === 'server') {
    const uses = graph.edges.filter((e) => e.server === selected.id).map((e) => e.useId);
    return { servers: new Set([selected.id]), uses: new Set(uses) };
  }
  const servers = graph.edges.filter((e) => e.useId === selected.id).map((e) => e.server);
  return { servers: new Set(servers), uses: new Set([selected.id]) };
}

const isLit = (p: EdgePath, lit: Lit) => lit.servers.has(p.server) && (p.useId === null || lit.uses.has(p.useId));

// ------------------------------------------------------------------ edges

/** Edge paths between the rendered boxes, measured after every render and on resize. */
function useEdgePaths(container: RefObject<HTMLDivElement>, graph: ArchitectureGraph): readonly EdgePath[] {
  const [paths, setPaths] = useState<readonly EdgePath[]>([]);
  useLayoutEffect(() => {
    const root = container.current;
    if (!root) return;
    const measure = () => setPaths(edgePaths(root, graph));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(root);
    return () => observer.disconnect();
  }, [graph]);
  return paths;
}

function edgePaths(root: HTMLElement, graph: ArchitectureGraph): EdgePath[] {
  const origin = root.getBoundingClientRect();
  const anchors = new Map<string, DOMRect>();
  root.querySelectorAll<HTMLElement>('[data-anchor]').forEach((el) => anchors.set(el.dataset.anchor!, el.getBoundingClientRect()));
  const left = (r: DOMRect) => [r.left - origin.left, r.top - origin.top + r.height / 2] as const;
  const right = (r: DOMRect) => [r.right - origin.left, r.top - origin.top + r.height / 2] as const;
  const curve = ([x1, y1]: readonly [number, number], [x2, y2]: readonly [number, number]) => {
    const dx = Math.max(24, (x2 - x1) / 2);
    return `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`;
  };

  const clients = anchors.get('clients');
  const result: EdgePath[] = [];
  for (const s of graph.servers) {
    const box = anchors.get(`server:${s.id}`);
    if (clients && box) result.push({ key: `client:${s.id}`, d: curve(right(clients), left(box)), kind: 'client', readonly: false, server: s.id, useId: null });
  }
  for (const e of graph.edges) {
    const from = anchors.get(`server:${e.server}`);
    const to = anchors.get(`use:${e.useId}`);
    if (!from || !to) continue;
    result.push({ key: `${e.server}->${e.useId}`, d: curve(right(from), left(to)), kind: e.use, readonly: e.role === 'readonly', server: e.server, useId: e.useId });
  }
  return result;
}
