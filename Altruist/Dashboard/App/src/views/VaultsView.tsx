import { useMemo, useState } from 'preact/hooks';
import { cellText, parseCell, rowUpdate, type VaultDefinition, type VaultQueryResult, type VaultRow, vaultsApi } from '@/api/vaults';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { navigate } from '@/app/router';
import type { ViewProps } from '@/app/views';
import { Badge, Button, Card, Empty, ErrorBox, Tabs } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

const PAGE = 50;
type Tab = 'rows' | 'schema' | 'sql';

/** Database vaults: browse and edit rows (saved through the vault, in one transaction), read the schema, run SQL. */
export function VaultsView({ rest }: ViewProps) {
  const api = vaultsApi(useTransport());
  const [defs, reload] = useResource(api.list, []);
  const selected = defs.data?.find((d) => d.typeKey === rest[0]) ?? defs.data?.[0];

  return (
    <Page title="Vaults" subtitle="Persistent models: rows, schema and SQL." resource={defs} reload={reload}>
      <ResourceView resource={defs} reload={reload}>
        {(list) =>
          list.length === 0 ? (
            <Empty icon="database">No vault is registered.</Empty>
          ) : (
            <div class="split">
              <Card flush>
                <div class="list">
                  {list.map((d) => (
                    <button type="button" class={`list-item${d.typeKey === selected?.typeKey ? ' list-on' : ''}`} onClick={() => navigate('vaults', d.typeKey)}>
                      <strong>{d.clrTypeShort}</strong>
                      <span class="muted mono">
                        {d.keyspace}.{d.tableName}
                      </span>
                    </button>
                  ))}
                </div>
              </Card>
              {selected && <VaultDetail key={selected.typeKey} def={selected} />}
            </div>
          )
        }
      </ResourceView>
    </Page>
  );
}

function VaultDetail({ def }: { def: VaultDefinition }) {
  const [tab, setTab] = useState<Tab>('rows');
  return (
    <div class="stack">
      <div class="row">
        <h2 class="grow">{def.clrTypeShort}</h2>
        {def.storeHistory && <Badge tone="accent">history</Badge>}
        {def.supportsSqlQuery ? <Badge tone="ok">SQL</Badge> : <Badge>no SQL</Badge>}
      </div>
      <Tabs
        tabs={[
          ['rows', 'Rows'],
          ['schema', 'Schema'],
          ['sql', 'SQL'],
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === 'rows' && <VaultRows def={def} />}
      {tab === 'schema' && <VaultSchema def={def} />}
      {tab === 'sql' && <VaultSql def={def} />}
    </div>
  );
}

type Draft = Readonly<Record<number, Readonly<Record<string, string>>>>;

function VaultRows({ def }: { def: VaultDefinition }) {
  const api = vaultsApi(useTransport());
  const notify = useNotify();
  const [skip, setSkip] = useState(0);
  const [page, reload] = useResource(() => api.items(def.typeKey, skip, PAGE), [def.typeKey, skip]);
  const [draft, setDraft] = useState<Draft>({});
  const [editing, setEditing] = useState<{ row: number; field: string } | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const editable = def.supportsSqlQuery && def.columns.some((c) => c.isPrimaryKey);
  const columnOf = (field: string) => def.columns.find((c) => c.fieldName.toLowerCase() === field.toLowerCase());
  const edits = Object.values(draft).reduce((n, row) => n + Object.keys(row).length, 0);

  const columns = useMemo<readonly Column<{ index: number; row: VaultRow }>[]>(
    () => (page.data?.fields ?? []).map((f) => ({ id: f, label: f, value: (r: { row: VaultRow }) => cellText(r.row[f]) })),
    [page.data],
  );

  const commit = async () => {
    setError(null);
    try {
      const items = Object.entries(draft).map(([index, fields]) => {
        const row = page.data!.items[Number(index)];
        const changes = Object.fromEntries(Object.entries(fields).map(([f, text]) => [f, parseCell(columnOf(f), text)]));
        return rowUpdate(def, row, changes);
      });
      const result = await api.update(def.typeKey, items);
      notify('ok', `${result.updated} row${result.updated === 1 ? '' : 's'} saved.`);
      setDraft({});
      reload();
    } catch (e) {
      setError(e as Error);
    }
  };

  return (
    <ResourceView resource={page} reload={reload}>
      {(p) => (
        <Card flush>
          {error && <ErrorBox error={error} />}
          <DataTable
            rows={p.items.map((row, index) => ({ index, row }))}
            columns={columns}
            rowKey={(r) => String(r.index)}
            empty="This vault has no rows."
            toolbar={
              <>
                {edits > 0 && (
                  <>
                    <Badge tone="warn">{edits} edited</Badge>
                    <Button small onClick={() => setDraft({})}>
                      Discard
                    </Button>
                    <Button small variant="primary" icon="save" onClick={commit}>
                      Save
                    </Button>
                  </>
                )}
                <span class="muted">
                  {p.total ? `${p.skip + 1}–${p.skip + p.items.length} of ${p.total}` : ''}
                </span>
                <Button small icon="chevron-left" disabled={skip === 0 || edits > 0} onClick={() => setSkip(Math.max(0, skip - PAGE))} />
                <Button small icon="chevron-right" disabled={skip + PAGE >= p.total || edits > 0} onClick={() => setSkip(skip + PAGE)} />
              </>
            }
            render={(r, col) => {
              const field = col.id;
              const pk = columnOf(field)?.isPrimaryKey ?? false;
              const dirty = draft[r.index]?.[field];
              if (editing?.row === r.index && editing.field === field) {
                return (
                  <input
                    class="mono cell-input"
                    autoFocus
                    value={dirty ?? cellText(r.row[field])}
                    onBlur={(e) => {
                      const text = e.currentTarget.value;
                      setDraft((d) => {
                        const { [field]: _, ...others } = d[r.index] ?? {};
                        const row = text === cellText(r.row[field]) ? others : { ...others, [field]: text };
                        const { [r.index]: __, ...rows } = d;
                        return Object.keys(row).length ? { ...rows, [r.index]: row } : rows;
                      });
                      setEditing(null);
                    }}
                    onKeyDown={(e) => (e.key === 'Enter' || e.key === 'Escape') && e.currentTarget.blur()}
                  />
                );
              }
              return (
                <span
                  class={`mono clip${dirty !== undefined ? ' cell-changed' : ''}${editable && !pk ? ' cell-editable' : ''}`}
                  title={editable && !pk ? 'Double-click to edit' : undefined}
                  onDblClick={editable && !pk ? () => setEditing({ row: r.index, field }) : undefined}
                >
                  {dirty ?? cellText(r.row[field])}
                </span>
              );
            }}
          />
        </Card>
      )}
    </ResourceView>
  );
}

const SCHEMA_COLUMNS: readonly Column<VaultDefinition['columns'][number]>[] = [
  { id: 'field', label: 'Field', value: (c) => c.fieldName },
  { id: 'column', label: 'Column', value: (c) => c.columnName },
  { id: 'type', label: 'Type', value: (c) => c.clrType },
  { id: 'flags', label: 'Flags', value: (c) => [c.isPrimaryKey && 'pk', c.isUnique && 'unique', c.isIndexed && 'index', c.isForeignKey && 'fk', c.isNullable && 'null'].filter(Boolean).join(' ') },
];

function VaultSchema({ def }: { def: VaultDefinition }) {
  return (
    <Card flush>
      <DataTable
        rows={def.columns}
        columns={SCHEMA_COLUMNS}
        rowKey={(c) => c.fieldName}
        filterable={def.columns.length > 12}
        render={(c, col) =>
          col.id === 'flags' ? (
            <div class="row">
              {c.isPrimaryKey && <Badge tone="accent">primary key</Badge>}
              {c.isUnique && <Badge>unique</Badge>}
              {c.isIndexed && <Badge>indexed</Badge>}
              {c.isForeignKey && <Badge>foreign key</Badge>}
              {c.isNullable && <Badge>nullable</Badge>}
            </div>
          ) : (
            <span class="mono">{String(col.value(c))}</span>
          )
        }
      />
    </Card>
  );
}

function VaultSql({ def }: { def: VaultDefinition }) {
  const api = vaultsApi(useTransport());
  const [sql, setSql] = useState(`SELECT * FROM "${def.keyspace}"."${def.tableName}" LIMIT 100;`);
  const [result, setResult] = useState<VaultQueryResult | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [running, setRunning] = useState(false);
  if (!def.supportsSqlQuery) return <Empty icon="database">This vault is not SQL-backed.</Empty>;

  const run = async () => {
    setRunning(true);
    setError(null);
    try {
      setResult(await api.query(def.typeKey, sql));
    } catch (e) {
      setError(e as Error);
    } finally {
      setRunning(false);
    }
  };

  const columns: readonly Column<{ i: number; row: VaultRow }>[] = (result?.columns ?? []).map((c) => ({ id: c, label: c, value: (r) => cellText(r.row[c]) }));
  return (
    <div class="stack">
      <Card>
        <div class="stack">
          <textarea
            class="json-edit sql-edit"
            rows={6}
            value={sql}
            spellcheck={false}
            onInput={(e) => setSql(e.currentTarget.value)}
            onKeyDown={(e) => (e.ctrlKey || e.metaKey) && e.key === 'Enter' && void run()}
          />
          <div class="row">
            <span class="muted grow">Runs against the live database. Ctrl+Enter runs.</span>
            <Button variant="primary" icon="play" disabled={running || !sql.trim()} onClick={run}>
              Run
            </Button>
          </div>
        </div>
      </Card>
      {error && <ErrorBox error={error} />}
      {result &&
        (result.hasRowset ? (
          <Card flush title={`${result.rows.length} row${result.rows.length === 1 ? '' : 's'}`}>
            <DataTable rows={result.rows.map((row, i) => ({ i, row }))} columns={columns} rowKey={(r) => String(r.i)} render={(r, c) => <span class="mono clip">{cellText(r.row[c.id])}</span>} />
          </Card>
        ) : (
          <Card>
            {result.statementKind}: {result.affectedRows ?? 0} row(s) affected.
          </Card>
        ))}
    </div>
  );
}
