import { useState } from 'preact/hooks';
import { type AIAgent, type AutosaveService, servicesApi, stateCounts } from '@/api/services';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Empty, KeyValues, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatAgo, formatBytes, formatDuration, formatNumber } from '@/ui/format';
import { useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const AGENT_COLUMNS: readonly Column<AIAgent>[] = [
  { id: 'id', label: 'Agent', value: (a) => a.instanceId },
  { id: 'behavior', label: 'Behavior', value: (a) => a.behavior, width: '200px' },
  { id: 'state', label: 'State', value: (a) => a.state, width: '200px' },
  { id: 'time', label: 'In state', value: (a) => a.timeInState, align: 'right', width: '100px' },
];

/** AI behaviors and every agent's state; zones with their players and spawns. */
export function GameplayView() {
  const api = servicesApi(useTransport());
  const [report, reload] = useResource(api.gameplay, [], 2000);
  return (
    <Page title="AI and zones" subtitle="Agents by behavior and state, and the world's zones. Refreshes every 2 s." resource={report} reload={reload}>
      <ResourceView resource={report} reload={reload}>
        {(r) => {
          const agents = r.agents.agents;
          return (
            <>
              <Stats>
                <Stat label="Agents" value={formatNumber(agents.length)} hint={r.agents.tick ? `as of AI tick ${formatNumber(r.agents.tick)}` : 'waiting for the next AI tick'} />
                <Stat label="Sleeping" value={formatNumber(agents.filter((a) => a.hibernated).length)} />
                <Stat label="Behaviors" value={formatNumber(r.behaviors.length)} />
                <Stat label="Active zones" value={`${r.zones.filter((z) => z.active).length} / ${r.zones.length}`} />
                <Stat label="Spawned" value={formatNumber(r.zones.reduce((n, z) => n + z.spawned, 0))} />
              </Stats>
              <div class="grid-2">
                <Card title="States">
                  {agents.length === 0 ? (
                    <Empty icon="hubot">No agent has a state machine.</Empty>
                  ) : (
                    <div class="bars">
                      {stateCounts(agents).map(([state, n]) => (
                        <div class="bar-row">
                          <span class="clip">{state}</span>
                          <div class="bar">
                            <div class="bar-fill" style={{ width: `${(n / agents.length) * 100}%` }} />
                          </div>
                          <span class="num">{n}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </Card>
                <Card title="Behaviors" flush>
                  <DataTable
                    rows={r.behaviors}
                    columns={[
                      { id: 'name', label: 'Behavior', value: (b) => b.name },
                      { id: 'type', label: 'Type', value: (b) => b.type },
                    ]}
                    rowKey={(b) => b.name}
                    filterable={false}
                    empty="No [AIBehavior] is registered."
                    render={(b, c) => <span class={c.id === 'type' ? 'mono muted' : undefined}>{String(c.value(b))}</span>}
                  />
                </Card>
              </div>
              <Card title="Agents" flush>
                <DataTable
                  rows={agents}
                  columns={AGENT_COLUMNS}
                  rowKey={(a) => a.instanceId}
                  empty="No agent."
                  render={(a, c) =>
                    c.id === 'time' ? formatDuration(a.timeInState) : c.id === 'id' ? <span class="mono">{a.instanceId} {a.hibernated && <Badge>asleep</Badge>}</span> : String(c.value(a))
                  }
                />
              </Card>
              <Card title="Zones" flush>
                <DataTable
                  rows={r.zones}
                  columns={[
                    { id: 'name', label: 'Zone', value: (z) => z.name },
                    { id: 'active', label: 'State', value: (z) => (z.active ? 'active' : 'asleep'), width: '100px' },
                    { id: 'players', label: 'Players', value: (z) => z.players, align: 'right', width: '90px' },
                    { id: 'spawned', label: 'Spawned', value: (z) => z.spawned, align: 'right', width: '100px' },
                    { id: 'capacity', label: 'Spawn capacity', value: (z) => z.spawnCapacity, align: 'right', width: '130px' },
                    { id: 'points', label: 'Spawn points', value: (z) => z.spawnPoints, align: 'right', width: '120px' },
                  ]}
                  rowKey={(z) => z.name}
                  empty="No zone is registered."
                  render={(z, c) => (c.id === 'active' ? <Badge tone={z.active ? 'ok' : 'muted'}>{String(c.value(z))}</Badge> : String(c.value(z)))}
                />
              </Card>
            </>
          );
        }}
      </ResourceView>
    </Page>
  );
}

/** Phone pairing: limits and every open session (a host screen and the phones that drive it). */
export function PairingView() {
  const api = servicesApi(useTransport());
  const [report, reload] = useResource(api.pairing, [], 3000);
  return (
    <Page title="Pairing" subtitle="Host screens and the phones paired to them as remote pads. Refreshes every 3 s." resource={report} reload={reload}>
      <ResourceView resource={report} reload={reload}>
        {(r) =>
          !r.enabled ? (
            <Empty icon="device-mobile">Pairing is off (altruist:server:pairing).</Empty>
          ) : (
            <>
              <Stats>
                <Stat label="Sessions" value={formatNumber(r.sessions.length)} hint={r.options ? `at most ${formatNumber(r.options.maxSessions)}` : undefined} />
                <Stat label="Phones" value={formatNumber(r.sessions.reduce((n, s) => n + s.companions.length, 0))} hint={r.options ? `up to ${r.options.maxCompanions} per host` : undefined} />
                <Stat label="Code length" value={r.options?.codeLength ?? '—'} />
                <Stat label="Message limit" value={formatBytes(r.options?.maxPayloadBytes)} />
              </Stats>
              <Card title="Sessions" flush>
                <DataTable
                  rows={r.sessions}
                  columns={[
                    { id: 'code', label: 'Code', value: (s) => s.code, width: '110px' },
                    { id: 'host', label: 'Host', value: (s) => s.hostClientId },
                    { id: 'phones', label: 'Phones', value: (s) => s.companions.map((c) => `${c.slot}: ${c.clientId}`).join(', ') },
                    { id: 'age', label: 'Open', value: (s) => s.createdUtc, width: '110px' },
                  ]}
                  rowKey={(s) => s.code}
                  empty="No session is open."
                  render={(s, c) =>
                    c.id === 'code' ? <code>{s.code}</code> : c.id === 'age' ? formatAgo(s.createdUtc) : c.id === 'phones' && s.companions.length === 0 ? <span class="muted">none yet</span> : <span class="mono clip">{String(c.value(s))}</span>
                  }
                />
              </Card>
            </>
          )
        }
      </ResourceView>
    </Page>
  );
}

/** Autosave: dirty entities, write-ahead logs, flushes and failures per model; flush everything now. */
export function AutosaveView() {
  const api = servicesApi(useTransport());
  const notify = useNotify();
  const [services, reload] = useResource(api.autosave, [], 3000);
  const [flushing, setFlushing] = useState(false);
  const flush = async () => {
    setFlushing(true);
    try {
      const after = await api.flushAutosave();
      const left = after.reduce((n, s) => n + s.dirty, 0);
      notify(left ? 'danger' : 'ok', left ? `Flushed; ${left} entities could not be saved and stay dirty.` : 'Everything is saved.');
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    } finally {
      setFlushing(false);
    }
  };
  return (
    <Page
      title="Autosave"
      subtitle="Entities waiting to be written to the database, per model. Refreshes every 3 s."
      resource={services}
      reload={reload}
      actions={
        <Button variant="primary" icon="save-all" disabled={flushing} onClick={flush}>
          Flush now
        </Button>
      }
    >
      <ResourceView resource={services} reload={reload}>
        {(list) =>
          list.length === 0 ? (
            <Empty icon="save">No autosave service is in use yet (they start when first used).</Empty>
          ) : (
            <>
              <Stats>
                <Stat label="Dirty" value={formatNumber(list.reduce((n, s) => n + s.dirty, 0))} tone={list.some((s) => s.dirty > 0) ? 'warn' : 'ok'} />
                <Stat label="Saved" value={formatNumber(list.reduce((n, s) => n + s.saved, 0))} />
                <Stat label="Failed saves" value={formatNumber(list.reduce((n, s) => n + s.failures, 0))} tone={list.some((s) => s.failures > 0) ? 'danger' : undefined} />
                <Stat label="In the log" value={formatNumber(list.reduce((n, s) => n + s.walBuffered, 0))} hint="buffered, not yet on disk" />
              </Stats>
              <div class="grid-2">
                {list.map((s: AutosaveService) => (
                  <Card title={s.type} actions={s.dirty > 0 ? <Badge tone="warn">{s.dirty} dirty</Badge> : <Badge tone="ok">saved</Badge>}>
                    <KeyValues
                      items={[
                        ['Writes to', s.hasVault ? `the vault, ${s.batchSize} per batch` : 'nothing (no vault: a flush only clears the dirty set)'],
                        ['Flushes', `${formatNumber(s.flushes)} · last ${formatAgo(s.lastFlushUtc)}`],
                        ['Saved', formatNumber(s.saved)],
                        ['Write-ahead log', s.walPath ? <span class="mono clip" title={s.walPath}>{s.walPath}</span> : 'off'],
                        ['Failed saves', s.failures ? <span class="error-text">{`${s.failures} · last ${formatAgo(s.lastErrorUtc)}: ${s.lastError}`}</span> : '0'],
                      ]}
                    />
                  </Card>
                ))}
              </div>
            </>
          )
        }
      </ResourceView>
    </Page>
  );
}
