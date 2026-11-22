import { useEffect, useMemo, useState } from 'preact/hooks';
import { describeAction, LAB_STORE_KEY, targetOf, type LabAction, labApi, type LabCall, type LabCollection, type LabResult, labSections } from '@/api/lab';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Button, Card, Empty, Icon, SearchInput } from '@/ui/basics';
import { prettyJson } from '@/ui/format';
import { useConfirm, useNotify } from '@/ui/overlays';
import { Page } from '@/ui/Page';
import { JsonView, ResourceView } from '@/ui/ResourceView';

const newId = () => Math.random().toString(36).slice(2, 10);

/** Calls any HTTP endpoint or portal gate of the server as a chosen client, and saves calls into runnable collections. */
export function LabView() {
  const transport = useTransport();
  const api = labApi(transport);
  const notify = useNotify();
  const { dialog, confirm } = useConfirm();
  const [actions, reload] = useResource(api.actions, []);
  const [filter, setFilter] = useState('');
  const [collections, setCollections] = useState<readonly LabCollection[]>([]);
  const [results, setResults] = useState<Readonly<Record<string, LabResult>>>({});

  useEffect(() => {
    void transport.store.get<LabCollection[]>(LAB_STORE_KEY).then((saved) => saved && setCollections(saved));
  }, []);
  const saveCollections = (next: readonly LabCollection[]) => {
    setCollections(next);
    void transport.store.set(LAB_STORE_KEY, next);
  };

  const sections = useMemo(() => {
    const words = filter.toLowerCase().split(/\s+/).filter(Boolean);
    const matching = (actions.data ?? []).filter((a) => {
      const text = `${describeAction(a)} ${a.handler} ${a.payloadType ?? ''} ${a.shield ?? ''}`.toLowerCase();
      return words.every((w) => text.includes(w));
    });
    return labSections(matching);
  }, [actions.data, filter]);

  const run = async (key: string, action: LabAction, bodyJson: string, clientId: string): Promise<boolean> => {
    try {
      const result = await api.invoke(action, bodyJson, clientId);
      setResults((r) => ({ ...r, [key]: result }));
      return result.success;
    } catch (error) {
      setResults((r) => ({ ...r, [key]: { success: false, message: (error as Error).message } }));
      return false;
    }
  };

  const addToCollection = (collectionId: string | null, action: LabAction, bodyJson: string, clientId: string) => {
    const call: LabCall = { id: newId(), actionId: action.id, bodyJson, clientId };
    if (collectionId) {
      saveCollections(collections.map((c) => (c.id === collectionId ? { ...c, calls: [...c.calls, call] } : c)));
    } else {
      const name = `Collection ${collections.length + 1}`;
      saveCollections([...collections, { id: newId(), name, calls: [call] }]);
    }
    notify('ok', 'Saved to the collection.');
  };

  const byId = new Map((actions.data ?? []).map((a) => [a.id, a]));

  return (
    <Page
      title="API Lab"
      subtitle="Call endpoints and gates as any client. Calls run against the live server."
      resource={actions}
      reload={reload}
      actions={<SearchInput value={filter} onInput={setFilter} placeholder="Filter endpoints and gates…" />}
    >
      {dialog}
      <ResourceView resource={actions} reload={reload}>
        {() => (
          <>
            {collections.map((c) => (
              <CollectionCard
                key={c.id}
                collection={c}
                byId={byId}
                results={results}
                onRun={async () => {
                  let ok = 0;
                  for (const call of c.calls) {
                    const action = byId.get(call.actionId);
                    if (action && (await run(`call:${call.id}`, action, call.bodyJson, call.clientId))) ok++;
                  }
                  notify(ok === c.calls.length ? 'ok' : 'danger', `${c.name}: ${ok} of ${c.calls.length} succeeded.`);
                }}
                onRunCall={(call) => {
                  const action = byId.get(call.actionId);
                  if (action) void run(`call:${call.id}`, action, call.bodyJson, call.clientId);
                }}
                onChange={(next) => saveCollections(collections.map((x) => (x.id === c.id ? next : x)))}
                onDelete={async () => {
                  if (await confirm({ title: 'Delete collection', body: `Delete "${c.name}" and its ${c.calls.length} saved call(s)?`, action: 'Delete' }))
                    saveCollections(collections.filter((x) => x.id !== c.id));
                }}
              />
            ))}
            {sections.length === 0 ? (
              <Empty icon="beaker">{filter ? 'Nothing matches the filter.' : 'The server has no endpoint or gate.'}</Empty>
            ) : (
              sections.map((s) => (
                <Card key={s.title} title={s.title} actions={<span class="muted">{s.actions.length}</span>} flush>
                  {s.actions.map((a) => (
                    <ActionRow key={a.id} action={a} result={results[a.id]} collections={collections} onRun={(body, client) => run(a.id, a, body, client)} onSave={(cid, body, client) => addToCollection(cid, a, body, client)} />
                  ))}
                </Card>
              ))
            )}
          </>
        )}
      </ResourceView>
    </Page>
  );
}

function ActionBadges({ action }: { action: LabAction }) {
  return (
    <>
      <Badge tone={action.kind === 'gate' ? 'accent' : 'muted'}>{action.kind === 'gate' ? 'GATE' : action.method || 'GET'}</Badge>
      {action.requiresAuth && (
        <span class="muted" title={`Requires auth: ${action.shield ?? 'yes'}`}>
          <Icon name="shield" />
        </span>
      )}
    </>
  );
}

function ResultView({ result }: { result: LabResult }) {
  const body = result.responseBody ?? result.message;
  return (
    <div class={`lab-result ${result.success ? 'lab-ok' : 'lab-fail'}`}>
      <div class="row">
        <Icon name={result.success ? 'pass' : 'error'} />
        <strong>{result.success ? 'Success' : 'Failed'}</strong>
        {result.statusCode != null && <span class="mono muted">HTTP {result.statusCode}</span>}
      </div>
      {body && <JsonView value={prettyJson(body)} />}
    </div>
  );
}

function ActionRow({
  action,
  result,
  collections,
  onRun,
  onSave,
}: {
  action: LabAction;
  result?: LabResult;
  collections: readonly LabCollection[];
  onRun: (body: string, client: string) => Promise<boolean>;
  onSave: (collectionId: string | null, body: string, client: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [body, setBody] = useState(action.sampleJson);
  const [client, setClient] = useState('');
  const [running, setRunning] = useState(false);
  // A gate always takes a payload; an HTTP call only when it has a body type.
  const hasBody = action.kind === 'gate' || !!action.payloadType || action.sampleJson.trim().length > 0;
  const send = async () => {
    setRunning(true);
    await onRun(body, client);
    setRunning(false);
    setOpen(true);
  };
  return (
    <div class={`lab-row${open ? ' lab-open' : ''}`}>
      <div class="lab-head" onClick={() => setOpen((o) => !o)}>
        <Icon name={open ? 'chevron-down' : 'chevron-right'} />
        <ActionBadges action={action} />
        <span class="mono grow clip">{targetOf(action)}</span>
        <span class="lab-status">{result && <Icon name={result.success ? 'pass' : 'error'} />}</span>
        <span class="muted mono clip lab-handler" title={action.handler}>
          {action.handler}
        </span>
        <Button small variant="primary" icon="play" disabled={running} onClick={(e) => (e.stopPropagation(), void send())}>
          Send
        </Button>
      </div>
      {open && (
        <div class="lab-body">
          {action.payloadType ? <div class="muted mono">Body: {action.payloadType}</div> : !hasBody && <div class="muted">No request body.</div>}
          {action.kind === 'gate' && <input value={client} placeholder="Client id (default: dashboard-lab)" onInput={(e) => setClient(e.currentTarget.value)} />}
          {hasBody && <textarea class="json-edit" rows={Math.min(14, Math.max(4, body.split('\n').length + 1))} value={body} spellcheck={false} onInput={(e) => setBody(e.currentTarget.value)} />}
          <div class="row">
            <select
              value=""
              onChange={(e) => {
                const v = e.currentTarget.value;
                e.currentTarget.value = '';
                if (v) onSave(v === '__new' ? null : v, body, client);
              }}
            >
              <option value="">Save to collection…</option>
              {collections.map((c) => (
                <option value={c.id}>{c.name}</option>
              ))}
              <option value="__new">New collection</option>
            </select>
            {hasBody && (
              <Button small onClick={() => setBody(action.sampleJson)}>
                Reset body
              </Button>
            )}
          </div>
          {result && <ResultView result={result} />}
        </div>
      )}
    </div>
  );
}

function CollectionCard({
  collection,
  byId,
  results,
  onRun,
  onRunCall,
  onChange,
  onDelete,
}: {
  collection: LabCollection;
  byId: ReadonlyMap<string, LabAction>;
  results: Readonly<Record<string, LabResult>>;
  onRun: () => Promise<void>;
  onRunCall: (call: LabCall) => void;
  onChange: (next: LabCollection) => void;
  onDelete: () => void;
}) {
  const [running, setRunning] = useState(false);
  const move = (index: number, by: number) => {
    const calls = [...collection.calls];
    const [call] = calls.splice(index, 1);
    calls.splice(Math.max(0, Math.min(calls.length, index + by)), 0, call);
    onChange({ ...collection, calls });
  };
  return (
    <Card
      title={<input class="title-input" value={collection.name} onChange={(e) => onChange({ ...collection, name: e.currentTarget.value || collection.name })} />}
      actions={
        <>
          <Button
            small
            variant="primary"
            icon="run-all"
            disabled={running || collection.calls.length === 0}
            onClick={async () => {
              setRunning(true);
              await onRun();
              setRunning(false);
            }}
          >
            Run all
          </Button>
          <Button small variant="danger" icon="trash" onClick={onDelete} />
        </>
      }
      flush
    >
      {collection.calls.length === 0 && <Empty>Save calls here from the list below.</Empty>}
      {collection.calls.map((call, i) => {
        const action = byId.get(call.actionId);
        const result = results[`call:${call.id}`];
        return (
          <div class="lab-row">
            <div class="lab-head">
              <span class="muted mono">{i + 1}</span>
              {action ? <ActionBadges action={action} /> : <Badge tone="warn">missing</Badge>}
              <span class="mono grow clip">{action ? targetOf(action) : `${call.actionId} (not on this server)`}</span>
              {result && <Icon name={result.success ? 'pass' : 'error'} />}
              <Button small icon="arrow-up" disabled={i === 0} onClick={() => move(i, -1)} />
              <Button small icon="arrow-down" disabled={i === collection.calls.length - 1} onClick={() => move(i, 1)} />
              <Button small icon="play" disabled={!action} onClick={() => onRunCall(call)} />
              <Button small icon="close" onClick={() => onChange({ ...collection, calls: collection.calls.filter((c) => c.id !== call.id) })} />
            </div>
            {result && !result.success && (
              <div class="lab-body">
                <ResultView result={result} />
              </div>
            )}
          </div>
        );
      })}
    </Card>
  );
}
