import { type Connection, connectionsApi, type SocketRoom } from '@/api/connections';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { useConfirm, useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const CONNECTION_COLUMNS: readonly Column<Connection>[] = [
  { id: 'id', label: 'Connection', value: (c) => c.connectionId },
  { id: 'room', label: 'Socket room', value: (c) => c.roomId },
  { id: 'ip', label: 'Address', value: (c) => c.ipAddress, width: '160px' },
  { id: 'actions', label: '', value: () => '', sortable: false, width: '1%' },
];

const ROOM_COLUMNS: readonly Column<SocketRoom>[] = [
  { id: 'id', label: 'Socket room', value: (r) => r.roomId },
  { id: 'count', label: 'Connections', value: (r) => r.connectionCount, align: 'right', width: '120px' },
  { id: 'actions', label: '', value: () => '', sortable: false, width: '1%' },
];

/** Live socket connections and the transport's rooms; operators can disconnect a client or empty a room. */
export function ConnectionsView() {
  const api = connectionsApi(useTransport());
  const notify = useNotify();
  const { dialog, confirm } = useConfirm();
  const [data, reload] = useResource(() => Promise.all([api.connections(), api.rooms()]).then(([connections, rooms]) => ({ connections, rooms })), [], 5000);

  const act = async (ok: boolean, run: () => Promise<void>, done: string) => {
    if (!ok) return;
    try {
      await run();
      notify('ok', done);
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    }
  };

  return (
    <Page title="Connections" subtitle="Every open socket and the transport rooms it is in. Refreshes every 5 s." resource={data} reload={reload}>
      {dialog}
      <ResourceView resource={data} reload={reload}>
        {({ connections, rooms }) => (
          <>
            <Stats>
              <Stat label="Connections" value={connections.length} />
              <Stat label="Socket rooms" value={rooms.length} />
              <Stat label="Addresses" value={new Set(connections.map((c) => c.ipAddress).filter(Boolean)).size} />
            </Stats>
            <Card title="Connections" flush>
              <DataTable
                rows={connections}
                columns={CONNECTION_COLUMNS}
                rowKey={(c) => c.connectionId}
                empty="No client is connected."
                render={(c, col) =>
                  col.id === 'actions' ? (
                    <Button
                      small
                      variant="danger"
                      icon="debug-disconnect"
                      onClick={async () =>
                        act(
                          await confirm({ title: 'Disconnect client', body: <>Close connection <code>{c.connectionId}</code>? Its disconnect hooks run as for a normal close.</>, action: 'Disconnect' }),
                          () => api.disconnect(c.connectionId),
                          'Client disconnected.',
                        )
                      }
                    >
                      Disconnect
                    </Button>
                  ) : (
                    <span class={col.id === 'room' ? undefined : 'mono'}>{String(col.value(c) ?? '—')}</span>
                  )
                }
              />
            </Card>
            <Card title="Socket rooms" flush>
              <DataTable
                rows={rooms}
                columns={ROOM_COLUMNS}
                rowKey={(r) => r.roomId}
                empty="No socket room."
                render={(r, col) =>
                  col.id === 'actions' ? (
                    <Button
                      small
                      variant="danger"
                      icon="trash"
                      onClick={async () =>
                        act(
                          await confirm({ title: 'Delete room', body: <>Disconnect all {r.connectionCount} connection(s) of <code>{r.roomId}</code> and delete the room?</>, action: 'Delete' }),
                          () => api.deleteRoom(r.roomId),
                          'Room deleted.',
                        )
                      }
                    >
                      Delete
                    </Button>
                  ) : col.id === 'count' ? (
                    <Badge tone={r.connectionCount ? 'accent' : 'muted'}>{r.connectionCount}</Badge>
                  ) : (
                    <span class="mono">{r.roomId}</span>
                  )
                }
              />
            </Card>
          </>
        )}
      </ResourceView>
    </Page>
  );
}
