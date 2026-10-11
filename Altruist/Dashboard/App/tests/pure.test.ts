import { describe, expect, it } from 'vitest';
import { durationOf, mergeEvents, type NetworkEvent, targetOf } from '@/api/network';
import { type LabAction, labSections } from '@/api/lab';
import { stateCounts } from '@/api/services';
import { stateName } from '@/api/server';
import { cellText, parseCell, rowUpdate, type VaultColumn, type VaultDefinition } from '@/api/vaults';
import { categoryOf, type ServiceInfo } from '@/api/summary';
import { splitNdjson } from '@/host/ndjson';
import { parseRoute, formatRoute } from '@/app/router';
import { formatBytes, formatDuration, formatMs, prettyJson } from '@/ui/format';
import { nextSort, visibleRows, type Column } from '@/ui/table';
import { angleOf, applyDelta, archetypeCounts, EMPTY_WORLD, type WorldDelta, type WorldObject } from '@/world/model';

const object = (id: string, x: number, archetype = 'car'): WorldObject => ({
  instanceId: id,
  archetype,
  name: id,
  zoneId: '',
  clientId: '',
  expired: false,
  transform: { position: { x, y: 0, z: 0 }, size: { x: 1, y: 1, z: 1 }, scale: { x: 1, y: 1, z: 1 }, rotation: { x: 0, y: 0, z: 0, w: 1 } },
  colliders: [{ id: 'c', shape: 'box', transform: { position: { x, y: 0, z: 0 }, size: { x: 1, y: 1, z: 1 }, scale: { x: 1, y: 1, z: 1 } }, isTrigger: false, transformSpace: 'world' }],
});

const delta = (d: Partial<WorldDelta>): WorldDelta => ({ reset: false, worldIndex: 0, timestampUtc: 't', added: [], moved: [], removed: [], gizmos: [], removedGizmoIds: [], ...d });

describe('world state', () => {
  it('a reset replaces everything', () => {
    const first = applyDelta(EMPTY_WORLD, delta({ reset: true, added: [object('a', 0)] }));
    const second = applyDelta(first, delta({ reset: true, added: [object('b', 0)] }));
    expect([...second.objects.keys()]).toEqual(['b']);
    expect(second.reset).toBe(true);
  });

  it('a move carries world-space colliders along and leaves the rest', () => {
    const start = applyDelta(EMPTY_WORLD, delta({ reset: true, added: [object('a', 0), object('b', 5)] }));
    const moved = applyDelta(start, delta({ moved: [{ instanceId: 'a', archetype: 'car', name: 'a', position: { x: 3, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 } }] }));
    expect(moved.objects.get('a')!.transform.position.x).toBe(3);
    expect(moved.objects.get('a')!.colliders[0].transform.position.x).toBe(3);
    expect(moved.objects.get('b')).toBe(start.objects.get('b'));
    expect([...moved.changed]).toEqual(['a']);
  });

  it('removals and gizmo changes apply; the state passed in is not changed', () => {
    const start = applyDelta(EMPTY_WORLD, delta({ reset: true, added: [object('a', 0)] }));
    const next = applyDelta(start, delta({ removed: ['a'], gizmos: [{ id: 'g', worldIndex: 0, category: 'c', source: 's', type: 'circle', label: '', color: '#fff', position: { x: 0, y: 0, z: 0 }, radius: 1, width: 0, height: 0, points: [], attachToInstanceId: '' }] }));
    expect(next.objects.size).toBe(0);
    expect(next.gizmos.has('g')).toBe(true);
    expect(start.objects.size).toBe(1);
  });

  it('counts archetypes, most common first', () => {
    expect(archetypeCounts([object('a', 0, 'car'), object('b', 0, 'ball'), object('c', 0, 'car')])).toEqual([
      ['car', 2],
      ['ball', 1],
    ]);
  });

  it('reads a 2D rotation back from its quaternion', () => {
    const q = { x: 0, y: 0, z: Math.sin(0.6), w: Math.cos(0.6) };
    expect(angleOf(q)).toBeCloseTo(1.2);
  });
});

describe('tables', () => {
  type Row = { name: string; n: number };
  const columns: Column<Row>[] = [
    { id: 'name', label: 'Name', value: (r) => r.name },
    { id: 'n', label: 'N', value: (r) => r.n },
  ];
  const rows: Row[] = [
    { name: 'beta', n: 10 },
    { name: 'alpha', n: 2 },
    { name: 'gamma', n: 2 },
  ];

  it('filters by every word and sorts numbers as numbers', () => {
    expect(visibleRows(rows, columns, 'a 2', null).map((r) => r.name)).toEqual(['alpha', 'gamma']);
    expect(visibleRows(rows, columns, '', { column: 'n', descending: false }).map((r) => r.name)).toEqual(['alpha', 'gamma', 'beta']);
  });

  it('cycles a header through ascending, descending, unsorted', () => {
    const a = nextSort(null, 'n');
    const b = nextSort(a, 'n');
    expect(a).toEqual({ column: 'n', descending: false });
    expect(b).toEqual({ column: 'n', descending: true });
    expect(nextSort(b, 'n')).toBeNull();
  });
});

describe('network', () => {
  const event = (id: number, extra: Partial<NetworkEvent> = {}): NetworkEvent => ({ id, timestampUtc: '', kind: 'http', direction: 'inbound', transport: 'http', payloadBytes: 0, payloadTruncated: false, ...extra });

  it('appends new events once, oldest dropped beyond the limit', () => {
    const merged = mergeEvents([event(1), event(2)], [event(2), event(3), event(4)], 3);
    expect(merged.map((e) => e.id)).toEqual([2, 3, 4]);
  });

  it('names targets and picks the duration recorded', () => {
    expect(targetOf(event(1, { method: 'GET', path: '/x' }))).toBe('GET /x');
    expect(targetOf(event(1, { kind: 'packet', route: '/game', gate: 'move' }))).toBe('/game :: move');
    expect(durationOf(event(1, { handlerDurationMs: 4 }))).toBe(4);
  });

  it('splits NDJSON across chunks', () => {
    const first = splitNdjson('', '{"a":1}\n{"b"');
    expect(first.values).toEqual([{ a: 1 }]);
    expect(splitNdjson(first.rest, ':2}\n').values).toEqual([{ b: 2 }]);
  });
});

describe('vault edits', () => {
  const column = (fieldName: string, clrType: string, extra: Partial<VaultColumn> = {}): VaultColumn => ({ fieldName, columnName: fieldName, clrType, isNullable: false, isPrimaryKey: false, isIndexed: false, isUnique: false, isForeignKey: false, ...extra });

  it('parses cells by column type and refuses bad numbers', () => {
    expect(parseCell(column('n', 'Int32'), '42')).toBe(42);
    expect(parseCell(column('b', 'Boolean'), 'TRUE')).toBe(true);
    expect(parseCell(column('s', 'String', { isNullable: true }), '')).toBeNull();
    expect(parseCell(column('j', 'Object'), '{"x":1}')).toEqual({ x: 1 });
    expect(() => parseCell(column('n', 'Int64'), 'abc')).toThrow();
  });

  it('sends the primary key with the changes', () => {
    const def = { columns: [column('Id', 'String', { isPrimaryKey: true }), column('Name', 'String')] } as unknown as VaultDefinition;
    expect(rowUpdate(def, { Id: 'p1', Name: 'old' }, { Name: 'new' })).toEqual({ Id: 'p1', Name: 'new' });
    expect(cellText({ a: 1 })).toBe('{"a":1}');
  });
});

describe('lab', () => {
  const action = (method: string, path: string, kind = 'http'): LabAction => ({ id: `${method}${path}`, kind, method, path, name: path, handler: 'H.M', sampleJson: '', requiresAuth: false });

  it('lists auth first, then methods in order, then gates', () => {
    const sections = labSections([action('POST', '/b'), action('GET', '/a'), action('POST', '/login'), action('', '/game', 'gate')]);
    expect(sections.map((s) => s.title)).toEqual(['Auth', 'GET', 'POST', 'Portal gates']);
  });
});

describe('small helpers', () => {
  it('format', () => {
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatMs(0.25)).toBe('250 µs');
    expect(formatDuration(3725)).toBe('1h 2m');
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}');
  });

  it('routes', () => {
    expect(parseRoute('#/vaults/players', 'overview')).toEqual({ view: 'vaults', rest: ['players'] });
    expect(parseRoute('', 'overview').view).toBe('overview');
    expect(formatRoute('worlds', '2')).toBe('#/worlds/2');
  });

  it('enum names arrive as names or numbers', () => {
    expect(stateName(1)).toBe('Ready');
    expect(stateName('Draining')).toBe('Draining');
    expect(categoryOf({ category: 0 } as ServiceInfo)).toBe('Portal');
  });

  it('counts agents per behavior and state', () => {
    const agent = (behavior: string, state: string) => ({ instanceId: state, behavior, state, timeInState: 0, hibernated: false });
    expect(stateCounts([agent('wolf', 'chase'), agent('wolf', 'idle'), agent('wolf', 'chase')])[0]).toEqual(['wolf · chase', 2]);
  });
});
