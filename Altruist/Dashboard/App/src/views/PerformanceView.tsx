import { networkApi, type NetworkEvent, type TimingSummary, targetOf } from '@/api/network';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Card, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatMs, formatNumber, formatTime } from '@/ui/format';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const TIMING_COLUMNS: readonly Column<TimingSummary>[] = [
  { id: 'name', label: 'Name', value: (t) => t.name },
  { id: 'count', label: 'Count', value: (t) => t.count, align: 'right', width: '80px' },
  { id: 'errors', label: 'Errors', value: (t) => t.errorRate, align: 'right', width: '80px' },
  { id: 'p50', label: 'p50', value: (t) => t.p50Ms, align: 'right', width: '80px' },
  { id: 'p95', label: 'p95', value: (t) => t.p95Ms, align: 'right', width: '80px' },
  { id: 'p99', label: 'p99', value: (t) => t.p99Ms, align: 'right', width: '80px' },
  { id: 'max', label: 'Max', value: (t) => t.maxMs, align: 'right', width: '80px' },
];

const SLOW_COLUMNS: readonly Column<NetworkEvent>[] = [
  { id: 'time', label: 'Time', value: (e) => e.timestampUtc, width: '110px' },
  { id: 'target', label: 'Target', value: targetOf },
  { id: 'duration', label: 'Duration', value: (e) => e.durationMs ?? e.handlerDurationMs, align: 'right', width: '90px' },
];

function renderTiming(t: TimingSummary, c: Column<TimingSummary>) {
  if (c.id === 'name') return <span class="mono">{t.name}</span>;
  if (c.id === 'count') return formatNumber(t.count);
  if (c.id === 'errors') return t.errorCount ? <span class="error-text">{formatNumber(t.errorRate, 1)}%</span> : <span class="muted">0</span>;
  return formatMs(c.value(t) as number);
}

function TimingCard({ title, summary }: { title: string; summary: TimingSummary }) {
  return (
    <Card title={title} flush>
      <DataTable rows={summary.byName} columns={TIMING_COLUMNS} rowKey={(t) => t.name} render={renderTiming} initialSort={{ column: 'p95', descending: true }} filterable={summary.byName.length > 8} empty="Nothing recorded yet." />
    </Card>
  );
}

function SlowCard({ title, events }: { title: string; events: readonly NetworkEvent[] }) {
  return (
    <Card title={title} flush>
      <DataTable
        rows={events}
        columns={SLOW_COLUMNS}
        rowKey={(e) => String(e.id)}
        filterable={false}
        initialSort={{ column: 'duration', descending: true }}
        empty="Nothing over the slow threshold."
        render={(e, c) => (c.id === 'time' ? <span class="mono">{formatTime(e.timestampUtc)}</span> : c.id === 'duration' ? formatMs(c.value(e) as number) : <span class="mono">{targetOf(e)}</span>)}
      />
    </Card>
  );
}

/** Latency percentiles of HTTP routes, portal gates and the packet pipeline, from the recorded traffic. */
export function PerformanceView() {
  const api = networkApi(useTransport());
  const [perf, reload] = useResource(api.performance, [], 5000);
  return (
    <Page title="Performance" subtitle="Percentiles over the recorded traffic window. Refreshes every 5 s." resource={perf} reload={reload}>
      <ResourceView resource={perf} reload={reload}>
        {(p) => (
          <>
            <Stats>
              <Stat label="HTTP p95" value={formatMs(p.http.p95Ms)} hint={`${formatNumber(p.http.count)} requests`} tone={p.http.errorRate > 1 ? 'warn' : undefined} />
              <Stat label="Gate p95" value={formatMs(p.gates.p95Ms)} hint={`${formatNumber(p.gates.count)} calls`} />
              <Stat label="Decode p95" value={formatMs(p.packetDecode.p95Ms)} />
              <Stat label="Encode p95" value={formatMs(p.packetEncode.p95Ms)} />
              <Stat label="Send p95" value={formatMs(p.transportSend.p95Ms)} />
            </Stats>
            <div class="grid-2">
              <TimingCard title="HTTP routes" summary={p.http} />
              <TimingCard title="Portal gates" summary={p.gates} />
              <TimingCard title="Packet decode" summary={p.packetDecode} />
              <TimingCard title="Packet encode and send" summary={{ ...p.packetEncode, byName: [...p.packetEncode.byName, ...p.transportSend.byName.map((t) => ({ ...t, name: `send ${t.name}` }))] }} />
              <SlowCard title="Slow requests" events={p.slowRequests} />
              <SlowCard title="Slow gates" events={p.slowGates} />
            </div>
          </>
        )}
      </ResourceView>
    </Page>
  );
}
