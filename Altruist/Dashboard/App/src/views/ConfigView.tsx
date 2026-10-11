import { useState } from 'preact/hooks';
import { type ConfigEntry, summaryApi } from '@/api/summary';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const COLUMNS: readonly Column<ConfigEntry>[] = [
  { id: 'key', label: 'Key', value: (c) => c.key },
  { id: 'value', label: 'Value', value: (c) => c.value },
  { id: 'mode', label: 'Mode', value: (c) => (c.modifiable ? 'live' : 'read-only'), width: '100px' },
];

/**
 * Every altruist configuration key. Live-editable keys can be changed in place; edits are kept as a draft and applied
 * together (the server validates them all before changing any). Changes live in memory only, not in config files.
 */
export function ConfigView() {
  const api = summaryApi(useTransport());
  const notify = useNotify();
  const [summary, reload] = useResource(api.get, []);
  const [draft, setDraft] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);
  const pending = Object.entries(draft);

  const save = async () => {
    setSaving(true);
    try {
      const result = await api.updateConfig(pending.map(([key, value]) => ({ key, value })));
      notify('ok', `${result.updated} config value${result.updated === 1 ? '' : 's'} applied.`);
      setDraft({});
      reload();
    } catch (error) {
      notify('danger', (error as Error).message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Page
      title="Configuration"
      subtitle="Live keys apply immediately (in memory, not written to config files)."
      resource={summary}
      reload={reload}
      actions={
        pending.length > 0 && (
          <>
            <Badge tone="warn">{pending.length} unsaved</Badge>
            <Button onClick={() => setDraft({})}>Discard</Button>
            <Button variant="primary" icon="save" disabled={saving} onClick={save}>
              Apply
            </Button>
          </>
        )
      }
    >
      <ResourceView resource={summary} reload={reload}>
        {(data) => (
          <Card flush>
            <DataTable
              rows={data.configs}
              columns={COLUMNS}
              rowKey={(c) => c.key}
              initialSort={{ column: 'mode', descending: false }}
              render={(c, col) => {
                if (col.id === 'key') return <span class="mono">{c.key}</span>;
                if (col.id === 'mode') return c.modifiable ? <Badge tone="accent">live</Badge> : <span class="muted">read-only</span>;
                if (!c.modifiable) return <span class="mono">{c.value ?? ''}</span>;
                const edited = Object.hasOwn(draft, c.key);
                return (
                  <input
                    class={`mono cell-input${edited ? ' cell-dirty' : ''}`}
                    value={edited ? draft[c.key] : (c.value ?? '')}
                    onInput={(e) => {
                      const value = e.currentTarget.value;
                      setDraft((d) => {
                        const { [c.key]: _, ...rest } = d;
                        return value === (c.value ?? '') ? rest : { ...rest, [c.key]: value };
                      });
                    }}
                  />
                );
              }}
            />
          </Card>
        )}
      </ResourceView>
    </Page>
  );
}
