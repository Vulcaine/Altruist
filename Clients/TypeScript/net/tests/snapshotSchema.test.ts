import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  decodeRow,
  missingFields,
  rowDecoder,
  rowEncoder,
  SNAPSHOT_SCHEMA_FORMAT,
  SnapshotSchema,
  type SnapshotSchemaDocument,
  validateSnapshotSchema,
} from '../src/index.ts';

/** Same shape as a C# SnapshotSchemaSet export. */
const doc: SnapshotSchemaDocument = {
  format: SNAPSHOT_SCHEMA_FORMAT,
  rows: {
    ship: {
      length: 6,
      fields: [
        { name: 'id', index: 0, kind: 'int' },
        { name: 'team', index: 1, kind: 'enum', labels: ['red', 'blue'] },
        { name: 'x', index: 2, kind: 'number' },
        { name: 'shielded', index: 3, kind: 'bool' },
        { name: 'kills', index: 4, kind: 'int', whenMissing: 0 },
        { name: 'boost', index: 5, kind: 'bool', whenMissing: 1 },
      ],
    },
    clock: {
      length: 2,
      fields: [
        { name: 'phase', index: 0, kind: 'enum', labels: ['warmup', 'live', 'over'], fallback: 'live' },
        { name: 'time', index: 1, kind: 'number' },
      ],
    },
  },
  packets: {
    state: {
      code: 3001,
      fields: [
        { name: 'tick', index: 1, kind: 'value' },
        { name: 'clock', index: 2, kind: 'row', row: 'clock' },
        { name: 'ships', index: 3, kind: 'rows', row: 'ship' },
        { name: 'heat', index: 4, kind: 'value', whenMissing: [] },
        { name: 'wave', index: 5, kind: 'value', whenMissing: 0 },
        { name: 'depth', index: 6, kind: 'value' },
      ],
    },
  },
};

describe('rowDecoder', () => {
  const decode = rowDecoder<Record<string, unknown>>(doc.rows.ship!);

  it('maps positions to names and kinds', () => {
    assert.deepStrictEqual(decode([7, 1, 12.5, 1, 3, 0]), { id: 7, team: 'blue', x: 12.5, shielded: true, kills: 3, boost: false });
    assert.deepStrictEqual(Object.keys(decode([7, 1, 12.5, 1, 3, 0])), ['id', 'team', 'x', 'shielded', 'kills', 'boost']);
  });

  it('accepts booleans as true / false and 1 / 0', () => {
    assert.equal(decode([1, 0, 0, true]).shielded, true);
    assert.equal(decode([1, 0, 0, false]).shielded, false);
    assert.equal(decode([1, 0, 0, 2]).shielded, false);
  });

  it('fills optional trailing fields of short rows (older writers) and passes required ones through', () => {
    assert.deepStrictEqual(decode([7, 0, 1, 0]), { id: 7, team: 'red', x: 1, shielded: false, kills: 0, boost: true });
    assert.equal(decode([7, 0, 1, 0, null]).kills, 0);
    const short = decode([7]);
    assert.ok('x' in short);
    assert.equal(short.x, undefined);
  });

  it('maps unknown enum values to the fallback, or undefined without one', () => {
    const clock = rowDecoder<{ phase: string; time: number }>(doc.rows.clock!);
    assert.equal(clock([9, 1]).phase, 'live');
    assert.equal(clock([2, 1]).phase, 'over');
    assert.equal(decode([1, 5]).team, undefined);
  });

  it('decodeRow is the one-off form', () => {
    assert.deepStrictEqual(decodeRow([0, 4], doc.rows.clock!), { phase: 'warmup', time: 4 });
  });

  it('round-trips with rowEncoder', () => {
    const ship = { id: 3, team: 'blue', x: -2.25, shielded: true, kills: 9, boost: false };
    const row = rowEncoder<typeof ship>(doc.rows.ship!)(ship);
    assert.deepStrictEqual(row, [3, 1, -2.25, 1, 9, 0]);
    assert.deepStrictEqual(decode(row), ship);
  });
});

describe('SnapshotSchema', () => {
  const schema = new SnapshotSchema(doc);

  it('decodes packets with nested rows and caches decoders', () => {
    const decode = schema.packet<Record<string, unknown>>('state');
    assert.equal(schema.packet('state'), decode);
    assert.equal(schema.row('ship'), schema.row('ship'));
    const p = decode([3001, 42, [1, 3.5], [[1, 0, 2, 0, 1, 1], [2, 1, 3, 1]], [0.5], 4, 2]);
    assert.deepStrictEqual(p, {
      tick: 42,
      clock: { phase: 'live', time: 3.5 },
      ships: [
        { id: 1, team: 'red', x: 2, shielded: false, kills: 1, boost: true },
        { id: 2, team: 'blue', x: 3, shielded: true, kills: 0, boost: true },
      ],
      heat: [0.5],
      wave: 4,
      depth: 2,
    });
  });

  it('applies packet defaults for keys older writers did not send, with fresh array copies', () => {
    const decode = schema.packet<{ heat: number[]; wave: number; depth?: number }>('state');
    const a = decode([3001, 1, [0, 0], []]);
    const b = decode([3001, 1, [0, 0], []]);
    assert.deepStrictEqual(a.heat, []);
    assert.notEqual(a.heat, b.heat);
    assert.equal(a.wave, 0);
    assert.ok('depth' in a);
    assert.equal(a.depth, undefined);
  });

  it('rejects malformed documents', () => {
    assert.throws(() => new SnapshotSchema({ ...doc, format: 'other/1' }), /unsupported format/);
    assert.throws(
      () => validateSnapshotSchema({ ...doc, rows: { r: { length: 1, fields: [{ name: 'a', index: 1, kind: 'int' }] } }, packets: {} }),
      /index 1, expected 0/,
    );
    assert.throws(
      () => validateSnapshotSchema({ ...doc, packets: { p: { code: 1, fields: [{ name: 'a', index: 1, kind: 'rows', row: 'nope' }] } } }),
      /unknown row 'nope'/,
    );
    assert.throws(() => schema.row('nope'), /no row 'nope'/);
  });

  it('missingFields lists schema fields a client type lacks', () => {
    assert.deepStrictEqual(missingFields(doc.rows.clock!, ['phase']), ['time']);
    assert.deepStrictEqual(missingFields(doc.rows.clock!, ['phase', 'time', 'extra']), []);
  });
});
