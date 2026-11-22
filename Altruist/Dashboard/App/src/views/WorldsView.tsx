import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { navigate } from '@/app/router';
import type { ViewProps } from '@/app/views';
import { Badge, Button, Empty, Icon, KeyValues, SearchInput } from '@/ui/basics';
import { formatNumber, formatTime } from '@/ui/format';
import { Page } from '@/ui/Page';
import { ResourceView } from '@/ui/ResourceView';
import { angleOf, applyDelta, archetypeCounts, EMPTY_WORLD, type WorldDelta, type WorldObject, type WorldState, type WorldSummary } from '@/world/model';
import { archetypeColor, WorldScene } from '@/world/scene';

type Link = 'connecting' | 'live' | 'retrying';

const fmt = (n: number) => (Math.abs(n) >= 1000 ? n.toFixed(0) : n.toFixed(2));

/** The game's worlds, 2D or 3D, followed live: every object with its colliders, the game's gizmos, an inspector. */
export function WorldsView({ rest }: ViewProps) {
  const transport = useTransport();
  const [worlds, reload] = useResource(() => transport.json<WorldSummary[]>({ method: 'GET', path: '/dashboard/v1/worlds' }), []);
  const selected = worlds.data?.find((w) => String(w.index) === rest[0]) ?? worlds.data?.[0];

  return (
    <Page
      fill
      title="Worlds"
      subtitle="Live objects, colliders and gizmos."
      resource={worlds}
      reload={reload}
      actions={
        worlds.data &&
        worlds.data.length > 1 && (
          <select value={selected?.index} onChange={(e) => navigate('worlds', e.currentTarget.value)}>
            {worlds.data.map((w) => (
              <option value={w.index}>
                {w.name || `World ${w.index}`} ({w.dimensions}D)
              </option>
            ))}
          </select>
        )
      }
    >
      <ResourceView resource={worlds} reload={reload}>
        {(list) => (list.length === 0 || !selected ? <Empty icon="globe">The game has no world (or no 2D/3D environment mode).</Empty> : <WorldViewer key={selected.index} world={selected} />)}
      </ResourceView>
    </Page>
  );
}

function WorldViewer({ world }: { world: WorldSummary }) {
  const transport = useTransport();
  const host = useRef<HTMLDivElement>(null);
  const scene = useRef<WorldScene | null>(null);
  const [state, setState] = useState<WorldState>(EMPTY_WORLD);
  const [link, setLink] = useState<Link>('connecting');
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set());
  const [hiddenGizmos, setHiddenGizmos] = useState<ReadonlySet<string>>(new Set());
  const [filter, setFilter] = useState('');
  const [follow, setFollow] = useState(false);
  const fitted = useRef(false);

  useEffect(() => {
    const s = new WorldScene(host.current!);
    s.setDimensions(world.dimensions);
    scene.current = s;
    const onClick = (e: MouseEvent) => {
      if (e.detail > 1) return;
      setSelectedId(s.pick(e.clientX, e.clientY));
    };
    s.canvas.addEventListener('click', onClick);
    return () => {
      s.canvas.removeEventListener('click', onClick);
      s.dispose();
      scene.current = null;
    };
  }, [world.index, world.dimensions]);

  // The live stream: the first line is the whole world; reconnects after a failure.
  useEffect(() => {
    const abort = new AbortController();
    let retry = 0;
    const run = async () => {
      while (!abort.signal.aborted) {
        setLink('connecting');
        try {
          await transport.lines<WorldDelta | { ping: string }>(
            `/dashboard/v1/worlds/${world.index}/live`,
            (line) => {
              if ('ping' in line) return;
              setLink('live');
              setError(null);
              setState((current) => applyDelta(current, line));
            },
            abort.signal,
          );
        } catch (e) {
          setError((e as Error).message);
        }
        if (abort.signal.aborted) return;
        setLink('retrying');
        await new Promise((r) => (retry = window.setTimeout(r, 2000)));
      }
    };
    void run();
    return () => {
      abort.abort();
      clearTimeout(retry);
    };
  }, [world.index]);

  useEffect(() => {
    const s = scene.current;
    if (!s) return;
    s.setWorld(state);
    if (state.reset && state.objects.size > 0 && !fitted.current) {
      fitted.current = true;
      s.focus(null);
    }
    if (follow && selectedId && state.changed.has(selectedId)) s.focus(selectedId);
  }, [state]);
  useEffect(() => scene.current?.setHidden(hidden, hiddenGizmos), [hidden, hiddenGizmos]);
  useEffect(() => scene.current?.select(selectedId), [selectedId]);

  const objects = useMemo(() => [...state.objects.values()], [state.objects]);
  const archetypes = useMemo(() => archetypeCounts(objects), [objects]);
  const gizmoCategories = useMemo(() => [...new Set([...state.gizmos.values()].map((g) => g.category || 'other'))].sort(), [state.gizmos]);
  const listed = useMemo(() => {
    const words = filter.toLowerCase().split(/\s+/).filter(Boolean);
    return objects
      .filter((o) => !hidden.has(o.archetype || '(none)'))
      .filter((o) => words.every((w) => `${o.instanceId} ${o.name} ${o.archetype} ${o.clientId}`.toLowerCase().includes(w)))
      .sort((a, b) => (a.name || a.instanceId).localeCompare(b.name || b.instanceId))
      .slice(0, 500);
  }, [objects, filter, hidden]);
  const selected = selectedId ? state.objects.get(selectedId) : undefined;
  const toggle = (set: ReadonlySet<string>, value: string) => {
    const next = new Set(set);
    if (!next.delete(value)) next.add(value);
    return next;
  };

  return (
    <div class="world">
      <aside class="world-side">
        <div class="world-side-head">
          <SearchInput value={filter} onInput={setFilter} placeholder="Find an object…" />
        </div>
        <div class="chips">
          {archetypes.map(([a, n]) => (
            <button type="button" class={`chip${hidden.has(a) ? ' chip-off' : ''}`} onClick={() => setHidden((h) => toggle(h, a))} title={hidden.has(a) ? 'Show' : 'Hide'}>
              <span class="swatch" style={{ background: `#${archetypeColor(a).getHexString()}` }} />
              {a} <span class="muted">{n}</span>
            </button>
          ))}
        </div>
        <div class="world-list">
          {listed.map((o) => (
            <button type="button" class={`list-item${o.instanceId === selectedId ? ' list-on' : ''}`} onClick={() => setSelectedId(o.instanceId)} onDblClick={() => scene.current?.focus(o.instanceId)}>
              <span>
                <span class="swatch" style={{ background: `#${archetypeColor(o.archetype || '(none)').getHexString()}` }} />
                <strong>{o.name || o.instanceId}</strong>
              </span>
              <span class="muted mono">
                {o.archetype || '—'}
                {o.clientId ? ` · ${o.clientId}` : ''}
              </span>
            </button>
          ))}
          {objects.length > listed.length && <div class="muted table-clip">{objects.length - listed.length} more hidden or filtered.</div>}
        </div>
      </aside>
      <section class="world-stage">
        <div class="world-toolbar">
          <Badge tone={link === 'live' ? 'ok' : link === 'retrying' ? 'danger' : 'warn'} title={error ?? undefined}>
            <Icon name={link === 'live' ? 'circle-filled' : 'sync'} spin={link !== 'live'} /> {link}
          </Badge>
          <span class="muted">
            {world.dimensions}D · {formatNumber(state.objects.size)} objects · {formatNumber(state.gizmos.size)} gizmos
            {state.updatedAt ? ` · ${formatTime(state.updatedAt)}` : ''}
          </span>
          <div class="grow" />
          {gizmoCategories.map((c) => (
            <button type="button" class={`chip${hiddenGizmos.has(c) ? ' chip-off' : ''}`} onClick={() => setHiddenGizmos((h) => toggle(h, c))}>
              <Icon name="symbol-misc" /> {c}
            </button>
          ))}
          <Button small icon="screen-full" title="Show everything" onClick={() => scene.current?.focus(null)} />
        </div>
        <div class="world-canvas" ref={host} />
        {link !== 'live' && error && <div class="world-error error-text">{error}</div>}
      </section>
      <aside class="world-inspector">
        {selected ? (
          <Inspector object={selected} dimensions={world.dimensions} follow={follow} onFollow={setFollow} onFocus={() => scene.current?.focus(selected.instanceId)} onClose={() => setSelectedId(null)} />
        ) : (
          <Empty icon="inspect">Click an object to inspect it. Double-click in the list to fly to it.</Empty>
        )}
      </aside>
    </div>
  );
}

function Inspector({ object: o, dimensions, follow, onFollow, onFocus, onClose }: { object: WorldObject; dimensions: 2 | 3; follow: boolean; onFollow: (f: boolean) => void; onFocus: () => void; onClose: () => void }) {
  const p = o.transform.position;
  const r = o.transform.rotation;
  return (
    <div class="stack inspector">
      <div class="row">
        <h2 class="grow clip">{o.name || o.instanceId}</h2>
        <Button small icon="close" onClick={onClose} />
      </div>
      <div class="row">
        <Button small icon="target" onClick={onFocus}>
          Focus
        </Button>
        <label class="row muted">
          <input type="checkbox" checked={follow} onChange={(e) => onFollow(e.currentTarget.checked)} /> Follow
        </label>
        {o.expired && <Badge tone="warn">expired</Badge>}
      </div>
      <KeyValues
        items={[
          ['Id', <span class="mono">{o.instanceId}</span>],
          ['Archetype', o.archetype || null],
          ['Zone', o.zoneId || null],
          ['Client', o.clientId ? <span class="mono">{o.clientId}</span> : 'server'],
          ['Position', <span class="mono">{dimensions === 2 ? `${fmt(p.x)}, ${fmt(p.y)}` : `${fmt(p.x)}, ${fmt(p.y)}, ${fmt(p.z)}`}</span>],
          ['Rotation', <span class="mono">{dimensions === 2 ? `${((angleOf(r) * 180) / Math.PI).toFixed(1)}°` : r ? `${fmt(r.x)}, ${fmt(r.y)}, ${fmt(r.z)}, ${fmt(r.w)}` : '—'}</span>],
          ['Size', <span class="mono">{`${fmt(o.transform.size.x)}, ${fmt(o.transform.size.y)}${dimensions === 3 ? `, ${fmt(o.transform.size.z)}` : ''}`}</span>],
        ]}
      />
      <h3>Colliders ({o.colliders.length})</h3>
      {o.colliders.map((c) => (
        <div class="collider">
          <div class="row">
            <Badge tone="accent">{c.shape}</Badge>
            {c.isTrigger && <Badge>trigger</Badge>}
            <span class="muted mono clip">{c.id}</span>
          </div>
          <div class="muted mono small">
            {c.transformSpace} · at {fmt(c.transform.position.x)}, {fmt(c.transform.position.y)}
            {dimensions === 3 ? `, ${fmt(c.transform.position.z)}` : ''} · size {fmt(c.transform.size.x)}, {fmt(c.transform.size.y)}
            {dimensions === 3 ? `, ${fmt(c.transform.size.z)}` : ''}
            {c.heightfield ? ` · ${c.heightfield.width}×${c.heightfield.height} heights` : ''}
            {c.vertices ? ` · ${c.vertices.length} vertices` : ''}
          </div>
        </div>
      ))}
    </div>
  );
}
