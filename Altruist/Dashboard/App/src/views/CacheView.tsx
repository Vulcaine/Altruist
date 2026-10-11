import { useState } from 'preact/hooks';
import { type CacheEntry, cacheApi } from '@/api/cache';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { JsonEditor } from '@/ui/JsonEditor';
import { useConfirm, useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { JsonView, ResourceView } from '@/ui/ResourceView';
import { Modal } from '@/ui/overlays';

const COLUMNS: readonly Column<CacheEntry>[] = [
  { id: 'type', label: 'Type', value: (e) => e.typeShortName || e.type, width: '180px' },
  { id: 'key', label: 'Key', value: (e) => e.key },
  { id: 'group', label: 'Group', value: (e) => e.groupId, width: '140px' },
  { id: 'source', label: 'Source', value: (e) => e.source, width: '100px' },
  { id: 'preview', label: 'Value', value: (e) => e.preview ?? JSON.stringify(e.value) },
  { id: 'actions', label: '', value: () => '', sortable: false, width: '1%' },
];

const keyOf = (e: CacheEntry) => `${e.type}|${e.groupId}|${e.key}|${e.source}`;

/** The cache: provider status and every entry, with view, edit (written through to the remote store) and delete. */
export function CacheView() {
  const api = cacheApi(useTransport());
  const notify = useNotify();
  const { dialog, confirm } = useConfirm();
  const [data, reload] = useResource(() => Promise.all([api.info(), api.entries()]).then(([info, entries]) => ({ info, entries })), []);
  const [editing, setEditing] = useState<CacheEntry | null>(null);
  const [viewing, setViewing] = useState<CacheEntry | null>(null);

  const remove = async (entry: CacheEntry) => {
    if (!(await confirm({ title: 'Delete cache entry', body: <>Remove <code>{entry.key}</code> ({entry.typeShortName}) from the cache{entry.source !== 'inmemory' ? ' and the remote store' : ''}?</>, action: 'Delete' }))) return;
    try {
      await api.remove(entry);
      notify('ok', 'Entry deleted.');
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    }
  };

  return (
    <Page title="Cache" subtitle="Cached entries by type, group and key. Edits are written through to a remote store." resource={data} reload={reload}>
      {dialog}
      {editing && (
        <JsonEditor
          title={`Edit ${editing.typeShortName} ${editing.key}`}
          value={editing.value}
          onClose={() => setEditing(null)}
          onSave={async (value) => {
            try {
              await api.save(editing, value);
              notify('ok', 'Entry saved.');
              reload();
            } catch (error) {
              notify('danger', (error as Error).message);
              throw error;
            }
          }}
        />
      )}
      {viewing && (
        <Modal wide title={`${viewing.typeShortName} ${viewing.key}`} onClose={() => setViewing(null)}>
          <JsonView value={viewing.value} />
        </Modal>
      )}
      <ResourceView resource={data} reload={reload}>
        {({ info, entries }) => (
          <>
            <Stats>
              <Stat label="Provider" value={info.provider} />
              <Stat label="Status" value={info.isConnected ? 'connected' : 'disconnected'} tone={info.isConnected ? 'ok' : 'danger'} />
              <Stat label="In-memory entries" value={info.inMemoryEntryCount} />
              <Stat label="Types" value={new Set(entries.map((e) => e.type)).size} />
            </Stats>
            <Card flush>
              <DataTable
                rows={entries}
                columns={COLUMNS}
                rowKey={keyOf}
                onRowClick={setViewing}
                empty="The cache is empty."
                render={(e, col) => {
                  switch (col.id) {
                    case 'type':
                      return <strong title={e.type}>{e.typeShortName || e.type}</strong>;
                    case 'source':
                      return <Badge tone={e.source === 'inmemory' ? 'muted' : 'accent'}>{e.source}</Badge>;
                    case 'preview':
                      return <span class="mono clip">{String(col.value(e) ?? '')}</span>;
                    case 'actions':
                      return (
                        <div class="row nowrap" onClick={(ev) => ev.stopPropagation()}>
                          <Button small icon="edit" onClick={() => setEditing(e)}>
                            Edit
                          </Button>
                          <Button small variant="danger" icon="trash" onClick={() => remove(e)} />
                        </div>
                      );
                    default:
                      return <span class="mono">{String(col.value(e) ?? '')}</span>;
                  }
                }}
              />
            </Card>
          </>
        )}
      </ResourceView>
    </Page>
  );
}
