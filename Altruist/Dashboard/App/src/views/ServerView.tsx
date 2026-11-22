import { useRef, useState } from 'preact/hooks';
import { type FleetClaim, type FleetNode, serverApi, type ServerReport, STATE_TONE, stateName } from '@/api/server';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Empty, KeyValues, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatAgo, formatBytes, formatDuration, formatNumber } from '@/ui/format';
import { useConfirm, useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView, Sparkline } from '@/ui/ResourceView';

const HISTORY = 60;

const NODE_COLUMNS: readonly Column<FleetNode>[] = [
  { id: 'id', label: 'Server', value: (n) => n.nodeId },
  { id: 'state', label: 'State', value: (n) => stateName(n.state), width: '100px' },
  { id: 'region', label: 'Region', value: (n) => n.region, width: '100px' },
  { id: 'load', label: 'Load', value: (n) => (n.maxLoad > 0 ? n.load / n.maxLoad : n.load), align: 'right', width: '120px' },
  { id: 'stats', label: 'Stats', value: (n) => Object.entries(n.stats).map(([k, v]) => `${k}=${v}`).join(' ') },
  { id: 'address', label: 'Address', value: (n) => n.publicAddress ?? n.internalAddress, width: '170px' },
  { id: 'seen', label: 'Seen', value: (n) => n.seenUtc, width: '100px' },
];

const CLAIM_COLUMNS: readonly Column<FleetClaim>[] = [
  { id: 'kind', label: 'Kind', value: (c) => c.kind, width: '120px' },
  { id: 'id', label: 'Unit', value: (c) => c.id },
  { id: 'node', label: 'Held by', value: (c) => c.nodeId },
];

/** This server's capacity and drain, the process's health, the fleet's servers and the units they hold. */
export function ServerView() {
  const api = serverApi(useTransport());
  const notify = useNotify();
  const { dialog, confirm } = useConfirm();
  const history = useRef<{ cpu: number[]; memory: number[]; load: number[] }>({ cpu: [], memory: [], load: [] });
  const [report, reload] = useResource(
    () =>
      api.report().then((r) => {
        const h = history.current;
        const push = (list: number[], v: number) => (list.push(v), list.length > HISTORY && list.shift());
        push(h.cpu, r.process.cpuPercent);
        push(h.memory, r.process.workingSetBytes);
        push(h.load, r.node.load);
        return r;
      }),
    [],
    2000,
  );
  const [timeout, setTimeoutSeconds] = useState('');

  const drain = async (r: ServerReport) => {
    const seconds = Number(timeout) || r.node.drainTimeoutSeconds;
    const ok = await confirm({
      title: 'Drain this server',
      body: (
        <>
          <p>
            <strong>{r.node.nodeId}</strong> stops taking new work; running work gets {seconds} s to finish, then it is stopped. A drain cannot be undone: the
            server is meant to shut down afterwards.
          </p>
          {r.fleet && r.fleet.nodes.length > 1 && <p class="muted">Players queued here are handed to another server of the fleet.</p>}
        </>
      ),
      action: 'Drain',
    });
    if (!ok) return;
    try {
      await api.drain(seconds);
      notify('ok', 'Drain started.');
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    }
  };

  return (
    <Page title="Server" subtitle="Capacity, drain, process health and the fleet. Refreshes every 2 s." resource={report} reload={reload}>
      {dialog}
      <ResourceView resource={report} reload={reload}>
        {(r) => (
          <>
            <Stats>
              <Stat label="State" value={<Badge tone={STATE_TONE[r.node.state] ?? 'muted'}>{r.node.state}</Badge>} hint={r.node.accepting ? 'accepting work' : 'not accepting'} />
              <Stat label="Load" value={r.node.maxLoad > 0 ? `${formatNumber(r.node.load, 1)} / ${formatNumber(r.node.maxLoad)}` : formatNumber(r.node.load, 1)} hint={<Sparkline values={history.current.load} width={110} height={18} />} />
              <Stat label="CPU" value={`${formatNumber(r.process.cpuPercent, 0)} %`} hint={<Sparkline values={history.current.cpu} width={110} height={18} />} tone={r.process.cpuPercent > 85 ? 'warn' : undefined} />
              <Stat label="Memory" value={formatBytes(r.process.workingSetBytes)} hint={<Sparkline values={history.current.memory} width={110} height={18} />} />
              <Stat label="Uptime" value={formatDuration(r.process.uptimeSeconds)} hint={`pid ${r.process.processId}`} />
            </Stats>
            <div class="grid-2">
              <Card
                title="Capacity and drain"
                actions={
                  !r.node.draining && (
                    <>
                      <input type="number" min={1} placeholder={`${r.node.drainTimeoutSeconds} s`} value={timeout} onInput={(e) => setTimeoutSeconds(e.currentTarget.value)} style={{ width: '90px' }} />
                      <Button small variant="danger" icon="debug-stop" onClick={() => drain(r)}>
                        Drain
                      </Button>
                    </>
                  )
                }
              >
                {r.node.utilization !== null && (
                  <div class="meter" title={`${formatNumber(r.node.utilization * 100, 0)} % used`}>
                    <div class="meter-fill" style={{ width: `${Math.min(100, r.node.utilization * 100)}%` }} />
                  </div>
                )}
                <KeyValues
                  items={[
                    ['Server', <span class="mono">{r.node.nodeId}</span>],
                    ['Limit', r.node.maxLoad > 0 ? formatNumber(r.node.maxLoad) : 'unlimited'],
                    ...r.node.kinds.map((k) => [k.kind, `${k.units} unit${k.units === 1 ? '' : 's'} · load ${formatNumber(k.load, 1)}`] as const),
                    [
                      'Drain',
                      r.node.draining ? (
                        <span>
                          {r.node.drained ? (r.node.forced ? 'forced after timeout' : 'drained') : 'in progress'}
                          {r.node.drainStartedUtc ? ` · started ${formatAgo(r.node.drainStartedUtc)}` : ''}
                        </span>
                      ) : (
                        `not draining (timeout ${r.node.drainTimeoutSeconds} s)`
                      ),
                    ],
                  ]}
                />
                {r.node.participants.length > 0 && (
                  <div class="row" style={{ marginTop: '10px' }}>
                    {r.node.participants.map((p) => (
                      <Badge tone={p.isDrained ? 'ok' : r.node.draining ? 'warn' : 'muted'}>
                        {p.kind}
                        {r.node.draining ? (p.isDrained ? ' ✓' : ' …') : ''}
                      </Badge>
                    ))}
                  </div>
                )}
              </Card>
              <Card title="Process">
                <KeyValues
                  items={[
                    ['Machine', `${r.process.machine} · ${r.process.processors} cores`],
                    ['Runtime', r.process.runtime],
                    ['Managed heap', `${formatBytes(r.process.gcHeapBytes)} (allocated ${formatBytes(r.process.gcTotalAllocatedBytes)} in total)`],
                    ['Collections', `gen0 ${r.process.gen0Collections} · gen1 ${r.process.gen1Collections} · gen2 ${r.process.gen2Collections} · pause ${formatNumber(r.process.gcPauseTimePercent, 2)} %`],
                    ['Threads', `${r.process.threads} (pool ${r.process.threadPoolThreads}, queued ${r.process.threadPoolQueue})`],
                    ['Lock contentions', formatNumber(r.process.lockContentions)],
                  ]}
                />
              </Card>
            </div>
            {r.fleet ? (
              <Card
                title={`Fleet · ${r.fleet.cluster}`}
                actions={
                  <>
                    <Badge tone={r.fleet.shared ? 'ok' : 'muted'}>{r.fleet.shared ? `shared (${r.fleet.backplane})` : 'single server'}</Badge>
                    {r.fleet.region && <Badge>{r.fleet.region}</Badge>}
                  </>
                }
                flush
              >
                <DataTable
                  rows={r.fleet.nodes}
                  columns={NODE_COLUMNS}
                  rowKey={(n) => n.nodeId}
                  filterable={r.fleet.nodes.length > 8}
                  render={(n, c) => {
                    switch (c.id) {
                      case 'id':
                        return (
                          <span class="mono">
                            {n.nodeId} {n.nodeId === r.fleet!.selfId && <Badge tone="accent">this</Badge>}
                          </span>
                        );
                      case 'state':
                        return <Badge tone={STATE_TONE[stateName(n.state)] ?? 'muted'}>{stateName(n.state)}</Badge>;
                      case 'load':
                        return n.maxLoad > 0 ? `${formatNumber(n.load, 1)} / ${formatNumber(n.maxLoad)}` : formatNumber(n.load, 1);
                      case 'seen':
                        return formatAgo(n.seenUtc);
                      default:
                        return <span class="mono clip">{String(c.value(n) ?? '—')}</span>;
                    }
                  }}
                />
              </Card>
            ) : (
              <Card title="Fleet">
                <Empty icon="server">This server is not in a fleet.</Empty>
              </Card>
            )}
            {r.claims.length > 0 && (
              <Card title={`Claimed units (${r.claims.length})`} flush>
                <DataTable
                  rows={r.claims}
                  columns={CLAIM_COLUMNS}
                  rowKey={(c) => `${c.kind}:${c.id}`}
                  render={(c, col) =>
                    col.id === 'node' ? (
                      <span class="mono">
                        {c.nodeId} {!c.nodeLive && <Badge tone="danger">gone</Badge>}
                      </span>
                    ) : (
                      <span class="mono">{String(col.value(c))}</span>
                    )
                  }
                />
              </Card>
            )}
            <div class="grid-2">
              <Card title="Connection tickets">
                {r.tickets ? (
                  <KeyValues
                    items={[
                      ['Query parameter', <code>{r.tickets.queryParam}</code>],
                      ['Ticket lifetime', formatDuration(r.tickets.lifetimeSeconds)],
                      ['Per principal', `at most ${r.tickets.maxPerPrincipal} outstanding`],
                      ['Session lifetime', formatDuration(r.tickets.sessionLifetimeSeconds)],
                    ]}
                  />
                ) : (
                  <Empty>Connection tickets are not used.</Empty>
                )}
              </Card>
              <Card title="Rate limits">
                <KeyValues
                  items={[
                    ...r.httpRateLimits.map((p) => [`HTTP ${p.name}`, `${p.permit} per ${formatDuration(p.windowSeconds)} by ${p.partition}${p.path ? ` on ${p.path}` : ''}`] as const),
                    ...(r.transportRateLimit?.enabled
                      ? Object.entries(r.transportRateLimit.buckets).map(([name, b]) => [`Packets ${name}`, `${b.capacity} burst, ${b.perSecond}/s${b.gates.length ? ` · ${b.gates.join(', ')}` : ''}`] as const)
                      : [['Packets', 'not limited'] as const]),
                  ]}
                />
              </Card>
            </div>
          </>
        )}
      </ResourceView>
    </Page>
  );
}
