import { useMemo, useState } from 'preact/hooks';
import { categoryOf, type ServiceCategory, type ServiceInfo, summaryApi } from '@/api/summary';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Card, KeyValues, Stat, Stats, Tabs } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';

type Kind = 'all' | 'portal' | 'service' | 'factory' | 'config' | 'prefab';

const KINDS: readonly (readonly [Kind, string])[] = [
  ['all', 'All'],
  ['portal', 'Portals'],
  ['service', 'Services'],
  ['factory', 'Factories'],
  ['config', 'Configurations'],
  ['prefab', 'Prefabs'],
];

const LABEL: Record<ServiceCategory, string> = { Portal: 'Portal', Service: 'Service', ServiceFactory: 'Factory', ServiceConfiguration: 'Configuration' };

/** Which tab a registered type belongs to; a type may match several. */
export function matchesKind(service: ServiceInfo, kind: Kind): boolean {
  const category = categoryOf(service);
  const text = [service.name, service.fullName, service.serviceType ?? ''].join(' ').toLowerCase();
  switch (kind) {
    case 'all':
      return true;
    case 'portal':
      return category === 'Portal' || !!service.endpoint;
    case 'service':
      return category === 'Service';
    case 'factory':
      return category === 'ServiceFactory';
    case 'config':
      return category === 'ServiceConfiguration';
    case 'prefab':
      return text.includes('prefab');
  }
}

const COLUMNS: readonly Column<ServiceInfo>[] = [
  { id: 'name', label: 'Type', value: (s) => s.fullName },
  { id: 'category', label: 'Kind', value: (s) => LABEL[categoryOf(s)], width: '120px' },
  { id: 'lifetime', label: 'Lifetime', value: (s) => s.lifetime, width: '110px' },
  { id: 'detail', label: 'Serves', value: (s) => s.endpoint ?? s.serviceType ?? s.context },
  { id: 'assembly', label: 'Assembly', value: (s) => s.assembly, width: '160px' },
];

export function SummaryView() {
  const api = summaryApi(useTransport());
  const [summary, reload] = useResource(api.get, []);
  const [kind, setKind] = useState<Kind>('all');
  const services = useMemo(() => (summary.data?.services ?? []).filter((s) => matchesKind(s, kind)), [summary.data, kind]);

  return (
    <Page title="Overview" subtitle="What this server runs: engine, configuration and every registered type." resource={summary} reload={reload}>
      <ResourceView resource={summary} reload={reload}>
        {(data) => (
          <>
            <Stats>
              <Stat label="Registered types" value={data.serviceCount} />
              <Stat label="Portals" value={data.services.filter((s) => categoryOf(s) === 'Portal').length} />
              <Stat label="Config keys" value={data.configs.length} hint={`${data.configs.filter((c) => c.modifiable).length} live-editable`} />
              <Stat label="Engine" value={data.engine ? `${data.engine.framerateHz} Hz` : 'off'} hint={data.engine?.diagnostics ? 'diagnostics on' : undefined} />
            </Stats>
            {data.engine && (
              <Card title="Engine">
                <KeyValues
                  items={[
                    ['Framerate', `${data.engine.framerateHz} per ${data.engine.unit}`],
                    ['Diagnostics', data.engine.diagnostics ? <Badge tone="ok">on</Badge> : <Badge>off</Badge>],
                    ['Throttle', data.engine.throttle ?? null],
                    ['Gravity', data.engine.gravity ? `${data.engine.gravity.x}, ${data.engine.gravity.y}, ${data.engine.gravity.z}` : null],
                  ]}
                />
              </Card>
            )}
            <Card title="Registered types" flush>
              <DataTable
                rows={services}
                columns={COLUMNS}
                rowKey={(s) => `${s.fullName}|${categoryOf(s)}|${s.serviceType ?? ''}|${s.endpoint ?? ''}`}
                initialSort={{ column: 'name', descending: false }}
                toolbar={<Tabs tabs={KINDS} value={kind} onChange={setKind} />}
                render={(s, c) =>
                  c.id === 'name' ? (
                    <div>
                      <strong>{s.name}</strong>
                      <div class="muted mono">{s.fullName}</div>
                    </div>
                  ) : c.id === 'category' ? (
                    <Badge tone={categoryOf(s) === 'Portal' ? 'accent' : 'muted'}>{LABEL[categoryOf(s)]}</Badge>
                  ) : (
                    <span class={c.id === 'detail' ? 'mono' : undefined}>{String(c.value(s) ?? '')}</span>
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
