import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { durationOf, mergeEvents, type NetworkEvent, networkApi, type NetworkEvents, targetOf } from '@/api/network';
import { useTransport } from '@/app/context';
import { Badge, Button, Card, ErrorBox, KeyValues, Tabs } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatBytes, formatMs, formatTime, prettyJson } from '@/ui/format';
import { Modal } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { JsonView } from '@/ui/ResourceView';

type Kind = 'all' | 'http' | 'packet';
const KEEP = 2000;
const POLL_MS = 1500;

const COLUMNS: readonly Column<NetworkEvent>[] = [
  { id: 'time', label: 'Time', value: (e) => e.id, width: '110px' },
  { id: 'dir', label: '', value: (e) => e.direction, width: '28px' },
  { id: 'kind', label: 'Kind', value: (e) => (e.statusCode ? `${e.kind} ${e.statusCode}` : e.kind), width: '96px' },
  { id: 'target', label: 'Target', value: targetOf },
  { id: 'client', label: 'Client', value: (e) => e.clientId ?? e.connectionId, width: '160px' },
  { id: 'duration', label: 'Duration', value: durationOf, align: 'right', width: '90px' },
  { id: 'size', label: 'Size', value: (e) => e.payloadBytes, align: 'right', width: '80px' },
];

const failed = (e: NetworkEvent) => !!e.error || (e.statusCode ?? 0) >= 500;

/** Recorded HTTP requests and packets, followed live (new events are fetched since the last one seen). */
export function NetworkView() {
  const api = networkApi(useTransport());
  const [state, setState] = useState<{ info: Omit<NetworkEvents, 'events'> | null; events: readonly NetworkEvent[]; error: Error | null }>({ info: null, events: [], error: null });
  const [live, setLive] = useState(true);
  const [kind, setKind] = useState<Kind>('all');
  const [selected, setSelected] = useState<NetworkEvent | null>(null);
  const lastId = useRef(0);

  useEffect(() => {
    let stopped = false;
    const poll = async () => {
      try {
        const page = await api.events(lastId.current || undefined);
        if (stopped) return;
        const { events, ...info } = page;
        if (events.length) lastId.current = Math.max(lastId.current, ...events.map((e) => e.id));
        setState((s) => ({ info, events: mergeEvents(s.events, events, KEEP), error: null }));
      } catch (error) {
        if (!stopped) setState((s) => ({ ...s, error: error as Error }));
      }
    };
    void poll();
    if (!live) return () => (stopped = true);
    const timer = setInterval(() => document.visibilityState === 'visible' && void poll(), POLL_MS);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [live]);

  const rows = useMemo(() => state.events.filter((e) => kind === 'all' || e.kind === kind), [state.events, kind]);
  const errors = state.events.filter(failed).length;

  return (
    <Page
      title="Network"
      subtitle={
        state.info
          ? state.info.enabled
            ? `Recording: last ${state.info.retentionMinutes} min, up to ${state.info.maxEvents} events, payloads to ${formatBytes(state.info.maxPayloadBytes)}.`
            : 'Recording is off (altruist:dashboard:network:enabled).'
          : undefined
      }
      actions={
        <>
          {errors > 0 && <Badge tone="danger">{errors} failed</Badge>}
          <Button icon={live ? 'debug-pause' : 'play'} onClick={() => setLive((l) => !l)}>
            {live ? 'Pause' : 'Follow'}
          </Button>
          <Button
            icon="clear-all"
            onClick={() => {
              setState((s) => ({ ...s, events: [] }));
            }}
          >
            Clear
          </Button>
        </>
      }
    >
      {state.error && <ErrorBox error={state.error} />}
      {selected && <EventDetail event={selected} onClose={() => setSelected(null)} />}
      <Card flush>
        <DataTable
          rows={rows}
          columns={COLUMNS}
          rowKey={(e) => String(e.id)}
          onRowClick={setSelected}
          initialSort={{ column: 'time', descending: true }}
          empty={state.info?.enabled === false ? 'Network recording is disabled.' : 'No traffic recorded yet.'}
          toolbar={
            <Tabs
              tabs={[
                ['all', 'All'],
                ['http', 'HTTP'],
                ['packet', 'Packets'],
              ]}
              value={kind}
              onChange={setKind}
            />
          }
          render={(e, col) => {
            switch (col.id) {
              case 'time':
                return <span class="mono">{formatTime(e.timestampUtc)}</span>;
              case 'dir':
                return <span title={e.direction} class={e.direction === 'inbound' ? 'dir-in' : 'dir-out'}>{e.direction === 'inbound' ? '↓' : '↑'}</span>;
              case 'kind':
                return <Badge tone={failed(e) ? 'danger' : e.kind === 'http' ? 'accent' : 'muted'}>{col.value(e)}</Badge>;
              case 'target':
                return (
                  <div>
                    <span class="mono">{targetOf(e) || '—'}</span>
                    {e.error && <div class="error-text">{e.error}</div>}
                  </div>
                );
              case 'duration':
                return formatMs(durationOf(e));
              case 'size':
                return formatBytes(e.payloadBytes);
              default:
                return <span class="mono clip">{String(col.value(e) ?? '—')}</span>;
            }
          }}
        />
      </Card>
    </Page>
  );
}

function EventDetail({ event: e, onClose }: { event: NetworkEvent; onClose: () => void }) {
  const payload = e.rawPayload ?? e.payloadPreview;
  return (
    <Modal wide title={targetOf(e) || `${e.kind} event`} onClose={onClose}>
      <KeyValues
        items={[
          ['When', formatTime(e.timestampUtc)],
          ['Kind', `${e.kind} · ${e.direction} · ${e.transport}`],
          ['Status', e.statusCode ?? null],
          ['Portal / gate', [e.portal, e.gate ?? e.event].filter(Boolean).join(' · ') || null],
          ['Packet', e.packetType ?? null],
          ['Client', e.clientId ?? null],
          ['Connection', e.connectionId ?? null],
          ['Room', e.roomId ?? null],
          ['Timing', [['total', e.durationMs], ['decode', e.decodeDurationMs], ['handler', e.handlerDurationMs], ['encode', e.encodeDurationMs], ['send', e.sendDurationMs]].filter(([, v]) => v != null).map(([k, v]) => `${k} ${formatMs(v as number)}`).join(' · ') || null],
          ['Payload', `${formatBytes(e.payloadBytes)}${e.payloadTruncated ? ' (truncated)' : ''}`],
          ['Error', e.error ? <span class="error-text">{e.error}</span> : null],
        ]}
      />
      {payload ? <JsonView value={prettyJson(payload)} /> : <div class="muted">No payload was recorded for this event.</div>}
    </Modal>
  );
}
