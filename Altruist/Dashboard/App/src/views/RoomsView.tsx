import { useState } from 'preact/hooks';
import { type ModuleReport, type ParticipantReport, type RoomHostEntry, type RoomReport, roomStatus, roomsApi, type SeatReport } from '@/api/rooms';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Empty, ErrorBox, KeyValues, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatAgo, formatDuration, formatNumber } from '@/ui/format';
import { useConfirm, useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const ROOM_COLUMNS: readonly Column<RoomReport>[] = [
  { id: 'id', label: 'Room', value: (r) => r.id },
  { id: 'mode', label: 'Mode', value: (r) => [r.mode, r.playlist].filter(Boolean).join(' · '), width: '160px' },
  { id: 'players', label: 'Players', value: (r) => r.participants.filter((p) => p.connected).length, align: 'right', width: '90px' },
  { id: 'bots', label: 'Bots', value: (r) => r.seats.filter((s) => s.bot).length, align: 'right', width: '70px' },
  { id: 'open', label: 'Open seats', value: (r) => r.seats.filter((s) => !s.owner && !s.bot).length, align: 'right', width: '100px' },
  { id: 'age', label: 'Age', value: (r) => r.startedAtUtc, width: '100px' },
  { id: 'status', label: 'Status', value: roomStatus, width: '100px' },
  { id: 'actions', label: '', value: () => '', sortable: false, width: '1%' },
];

const SEAT_COLUMNS: readonly Column<SeatReport>[] = [
  { id: 'id', label: 'Seat', value: (s) => s.id, width: '70px' },
  { id: 'team', label: 'Team', value: (s) => s.team, width: '70px' },
  { id: 'holder', label: 'Held by', value: (s) => s.owner ?? s.bot ?? '' },
];

const PLAYER_COLUMNS: readonly Column<ParticipantReport>[] = [
  { id: 'principal', label: 'Player', value: (p) => p.principalId },
  { id: 'seat', label: 'Seat', value: (p) => p.seat, align: 'right', width: '70px' },
  { id: 'state', label: 'State', value: (p) => (p.abandoned ? 'abandoned' : p.leftEarly ? 'left' : p.connected ? 'connected' : 'away'), width: '120px' },
  { id: 'buffer', label: 'Buffer', value: (p) => p.input.depth, align: 'right', width: '90px' },
  { id: 'jitter', label: 'Jitter', value: (p) => p.input.jitter, align: 'right', width: '80px' },
  { id: 'starved', label: 'Starved', value: (p) => p.input.starved, align: 'right', width: '90px' },
  { id: 'late', label: 'Late / dropped', value: (p) => p.input.stale + p.input.dropped, align: 'right', width: '120px' },
];

/** The game's room hosts: rooms with seats and players (input health included), queues and lobbies. */
export function RoomsView() {
  const api = roomsApi(useTransport());
  const [hosts, reload] = useResource(api.hosts, [], 2000);
  return (
    <Page title="Rooms" subtitle="Rooms, seats, players, queues and lobbies of every room host. Refreshes every 2 s." resource={hosts} reload={reload}>
      <ResourceView resource={hosts} reload={reload}>
        {(list) =>
          list.length === 0 ? (
            <Empty icon="organization">The game has no room host (RoomHost among the world steppers, or an IRoomHostInspection service).</Empty>
          ) : (
            list.map((h) => <HostCard key={h.index} host={h} reload={reload} />)
          )
        }
      </ResourceView>
    </Page>
  );
}

function HostCard({ host, reload }: { host: RoomHostEntry; reload: () => void }) {
  const api = roomsApi(useTransport());
  const notify = useNotify();
  const { dialog, confirm } = useConfirm();
  const [open, setOpen] = useState<string | null>(null);
  const r = host.report;
  if (!r)
    return (
      <Card title={host.game}>
        <ErrorBox error={new Error(host.error ?? 'No report.')} />
      </Card>
    );

  const close = async (room: RoomReport) => {
    const ok = await confirm({
      title: 'Close room',
      body: (
        <>
          Close <code>{room.id}</code> now? The game handles it as a forced close; its {room.participants.length} player(s) go back to the menu or their lobby.
        </>
      ),
      action: 'Close room',
    });
    if (!ok) return;
    try {
      await api.close(host.index, room.id);
      notify('ok', 'Room closed.');
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    }
  };

  const selected = r.rooms.find((x) => x.id === open);
  return (
    <>
      {dialog}
      <Stats>
        <Stat label={host.game} value={`${r.rooms.length} room${r.rooms.length === 1 ? '' : 's'}`} hint={r.draining ? 'draining: no new rooms' : `${r.fixedHz} Hz simulation`} tone={r.draining ? 'warn' : undefined} />
        <Stat label="Sessions" value={formatNumber(r.sessions)} />
        <Stat label="Players in rooms" value={formatNumber(r.rooms.reduce((n, x) => n + x.participants.filter((p) => p.connected).length, 0))} />
        <Stat label="Bots" value={formatNumber(r.rooms.reduce((n, x) => n + x.seats.filter((s) => s.bot).length, 0))} />
        <Stat label="Step" value={formatNumber(r.step)} />
      </Stats>
      <Card title="Rooms" flush>
        <DataTable
          rows={r.rooms}
          columns={ROOM_COLUMNS}
          rowKey={(x) => x.id}
          selected={open}
          onRowClick={(x) => setOpen(open === x.id ? null : x.id)}
          empty="No room is open."
          render={(x, c) => {
            switch (c.id) {
              case 'id':
                return (
                  <span class="mono">
                    {x.id.slice(0, 12)} {x.lobbyCode && <Badge>lobby {x.lobbyCode}</Badge>}
                  </span>
                );
              case 'age':
                return formatAgo(x.startedAtUtc);
              case 'status':
                return <Badge tone={roomStatus(x) === 'running' ? 'ok' : 'muted'}>{roomStatus(x)}</Badge>;
              case 'actions':
                return (
                  <span onClick={(e) => e.stopPropagation()}>
                    <Button small variant="danger" icon="close" onClick={() => close(x)}>
                      Close
                    </Button>
                  </span>
                );
              default:
                return String(c.value(x) ?? '');
            }
          }}
        />
      </Card>
      {selected && (
        <div class="grid-2">
          <Card title={`Seats of ${selected.id.slice(0, 12)}`} flush>
            <DataTable
              rows={selected.seats}
              columns={SEAT_COLUMNS}
              rowKey={(s) => String(s.id)}
              filterable={false}
              render={(s, c) =>
                c.id === 'holder' ? (
                  s.owner ? (
                    <span class="mono">{s.owner}</span>
                  ) : s.bot ? (
                    <Badge tone="accent">bot · {s.bot}</Badge>
                  ) : (
                    <span class="muted">open{s.openSeconds != null ? ` for ${formatDuration(s.openSeconds)}` : ''}{s.pending ? ' · being taken' : ''}</span>
                  )
                ) : (
                  String(c.value(s))
                )
              }
            />
          </Card>
          <Card title="Players" flush>
            <DataTable
              rows={selected.participants}
              columns={PLAYER_COLUMNS}
              rowKey={(p) => p.principalId}
              filterable={false}
              empty="No human player."
              render={(p, c) => {
                switch (c.id) {
                  case 'principal':
                    return <span class="mono">{p.principalId}</span>;
                  case 'state':
                    return (
                      <Badge tone={p.connected ? 'ok' : p.abandoned ? 'danger' : 'warn'}>
                        {String(c.value(p))}
                        {p.disconnectedSeconds != null ? ` ${formatDuration(p.disconnectedSeconds)}` : ''}
                      </Badge>
                    );
                  case 'buffer':
                    return `${p.input.depth} / ${p.input.targetDepth}${p.input.speculating ? ` (+${p.input.speculating})` : ''}`;
                  case 'jitter':
                    return formatNumber(p.input.jitter, 1);
                  default:
                    return formatNumber(c.value(p) as number);
                }
              }}
            />
          </Card>
        </div>
      )}
      {r.modules.map((m) => (
        <ModuleCard module={m} />
      ))}
    </>
  );
}

/** A module's report, drawn as it came: facts, then its tables. */
function ModuleCard({ module: m }: { module: ModuleReport }) {
  return (
    <Card title={m.name} flush>
      {m.facts.length > 0 && (
        <div class="card-note">
          <KeyValues items={m.facts.map((f) => [f.key, f.value] as const)} />
        </div>
      )}
      {m.tables.map((t) => (
        <div class="module-table">
          <h3>{t.title}</h3>
          <DataTable
            rows={t.rows.map((cells, i) => ({ i, cells }))}
            columns={t.columns.map((label, ci) => ({ id: String(ci), label, value: (row: { cells: readonly string[] }) => row.cells[ci] }))}
            rowKey={(row) => String(row.i)}
            filterable={t.rows.length > 10}
          />
        </div>
      ))}
      {m.facts.length === 0 && m.tables.length === 0 && <Empty>Nothing to report.</Empty>}
    </Card>
  );
}
