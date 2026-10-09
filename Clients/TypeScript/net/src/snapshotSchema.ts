/**
 * Schema-driven snapshot decoding: the TypeScript twin of Altruist's C# `SnapshotRowSchema<T>` /
 * `SnapshotSchemaSet`. The server declares its positional snapshot rows (and the packets that carry
 * them) once in C#, exports them as JSON (format {@link SNAPSHOT_SCHEMA_FORMAT}), and the client
 * decodes with that JSON instead of hand-written indexes, so a field is declared in one place.
 *
 * Use it for compact, frequently sent numeric state (per-tick snapshots). Plain MessagePack packets
 * with a few mixed-type members can keep a hand-written decoder.
 *
 * Decoding is a pure function of the input (no state, no clock), safe to use in deterministic replays.
 */

/** Format tag of the documents this module reads (`SnapshotSchemaSet.Format` in C#). */
export const SNAPSHOT_SCHEMA_FORMAT = 'altruist.snapshot-schema/1';

/**
 * How a row slot is interpreted (every slot is a float32 number on the wire):
 * - `number`: passed through;
 * - `int`: passed through (integral by contract, exact up to ±2^24);
 * - `bool`: `true` when the value is `1` or `true`;
 * - `enum`: `labels[value]`, else `fallback` (else `undefined`).
 */
export type RowFieldKind = 'number' | 'int' | 'bool' | 'enum';

/** One slot of a row schema, as exported by C# `SnapshotSchemaSet.ToJson()`. */
export interface RowField {
  /** Property name the decoder produces. */
  name: string;
  /** Position in the row. */
  index: number;
  kind: RowFieldKind;
  /**
   * Value used when a row from an older writer ends before this slot or holds nil. Absent = the
   * field is required and the raw value (possibly `undefined`) is passed through.
   */
  whenMissing?: number;
  /** Enum labels by value (`enum` only). */
  labels?: readonly string[];
  /** Label for an out-of-range enum value (`enum` only). */
  fallback?: string;
}

/** A positional row: `fields` in wire order, `length` = number of slots a current writer sends. */
export interface RowSchema {
  length: number;
  fields: readonly RowField[];
}

/**
 * How a packet member is decoded:
 * - `value`: passed through (`whenMissing` when absent or nil, if declared);
 * - `row`: one row of the row schema named `row`, decoded to an object;
 * - `rows`: an array of such rows, decoded to an array of objects.
 */
export type PacketFieldKind = 'value' | 'row' | 'rows';

/** One member (MessagePack `[Key(index)]`) of a packet schema. */
export interface PacketField {
  name: string;
  index: number;
  kind: PacketFieldKind;
  /** Row schema name (`row` / `rows` only). */
  row?: string;
  /** Value used when the packet array ends before this key or holds nil (arrays / objects are copied per decode). */
  whenMissing?: unknown;
}

/** A packet: its message code (C# `IPacketBase.MessageCode`, null when not a packet base) and members in key order. */
export interface PacketSchema {
  code: number | null;
  fields: readonly PacketField[];
}

/** The whole exported document (e.g. `shared/protocol/snapshot-schema.json`). */
export interface SnapshotSchemaDocument {
  format: string;
  rows: Record<string, RowSchema>;
  packets: Record<string, PacketSchema>;
}

/** Decodes one positional row into an object. */
export type RowDecoder<T> = (row: ArrayLike<unknown>) => T;

/** Decodes one packet array (the MessagePack array of the packet's keys, index 0 = message code). */
export type PacketDecoder<T> = (packet: ArrayLike<unknown>) => T;

const ROW_KINDS: readonly string[] = ['number', 'int', 'bool', 'enum'];
const PACKET_KINDS: readonly string[] = ['value', 'row', 'rows'];

type Reader = (row: ArrayLike<unknown>) => unknown;

function copyDefault(value: unknown): () => unknown {
  if (Array.isArray(value)) return () => value.slice();
  if (value !== null && typeof value === 'object') return () => JSON.parse(JSON.stringify(value)) as unknown;
  return () => value;
}

function fieldReader(f: RowField): Reader {
  const i = f.index;
  const wm = f.whenMissing;
  switch (f.kind) {
    case 'number':
    case 'int':
      return wm === undefined ? (r) => r[i] : (r) => r[i] ?? wm;
    case 'bool':
      return wm === undefined
        ? (r) => r[i] === true || r[i] === 1
        : (r) => {
            const v = r[i];
            return v === undefined || v === null ? wm !== 0 : v === true || v === 1;
          };
    case 'enum': {
      const labels = f.labels ?? [];
      const fb = f.fallback;
      return (r) => {
        let v = r[i];
        if ((v === undefined || v === null) && wm !== undefined) v = wm;
        const label = labels[v as number];
        return label === undefined ? fb : label;
      };
    }
    default:
      throw new Error(`snapshot schema: unknown row field kind '${String((f as RowField).kind)}' (${f.name})`);
  }
}

/**
 * Builds a decoder for rows of `schema`: an object with one property per field, in field order.
 * Build it once (module level) and reuse it; each call allocates only the result object.
 * Rows shorter than the schema (older writers) get `whenMissing` for the absent optional fields.
 * Prefer {@link SnapshotSchema.row} when you hold the whole document.
 *
 * @example
 * const decodeShip = rowDecoder<Ship>(doc.rows.ship!);
 * const ship = decodeShip([7, 1, 12.5, 1]); // { id: 7, team: 'orange', x: 12.5, shielded: true }
 */
export function rowDecoder<T>(schema: RowSchema): RowDecoder<T> {
  const names = schema.fields.map((f) => f.name);
  const readers = schema.fields.map(fieldReader);
  const n = readers.length;
  return (row) => {
    const out: Record<string, unknown> = {};
    for (let k = 0; k < n; k++) out[names[k]!] = readers[k]!(row);
    return out as T;
  };
}

/** One-off decode of a row; use {@link rowDecoder} on hot paths (it prepares the field readers once). */
export function decodeRow<T>(row: ArrayLike<unknown>, schema: RowSchema): T {
  return rowDecoder<T>(schema)(row);
}

/**
 * Builds the inverse of {@link rowDecoder}: object → positional numbers (bools as 1 / 0, enums as
 * their label's index, missing values as `whenMissing` or 0). For tests, tools and TypeScript
 * servers that must produce the same rows as the C# writer; the MessagePack encoder decides the
 * float width on the wire (the C# writer sends float32).
 */
export function rowEncoder<T>(schema: RowSchema): (value: T) => number[] {
  const fields = schema.fields;
  return (value) => {
    const src = value as Record<string, unknown>;
    const out = new Array<number>(schema.length).fill(0);
    for (const f of fields) {
      const v = src[f.name];
      let n: number;
      if (f.kind === 'bool') n = v === true || v === 1 ? 1 : 0;
      else if (f.kind === 'enum') n = typeof v === 'number' ? v : Math.max(0, (f.labels ?? []).indexOf(v as string));
      else n = typeof v === 'number' ? v : (f.whenMissing ?? 0);
      out[f.index] = n;
    }
    return out;
  };
}

/** Checks the shape of a parsed document and throws a descriptive error on anything unknown. */
export function validateSnapshotSchema(doc: SnapshotSchemaDocument): void {
  if (doc.format !== SNAPSHOT_SCHEMA_FORMAT) throw new Error(`snapshot schema: unsupported format '${doc.format}' (expected ${SNAPSHOT_SCHEMA_FORMAT})`);
  for (const [name, row] of Object.entries(doc.rows)) {
    row.fields.forEach((f, i) => {
      if (f.index !== i) throw new Error(`snapshot schema: row '${name}' field '${f.name}' has index ${f.index}, expected ${i}`);
      if (!ROW_KINDS.includes(f.kind)) throw new Error(`snapshot schema: row '${name}' field '${f.name}' has unknown kind '${f.kind}'`);
    });
    if (row.length !== row.fields.length) throw new Error(`snapshot schema: row '${name}' length ${row.length} != ${row.fields.length} fields`);
  }
  for (const [name, packet] of Object.entries(doc.packets)) {
    for (const f of packet.fields) {
      if (!PACKET_KINDS.includes(f.kind)) throw new Error(`snapshot schema: packet '${name}' field '${f.name}' has unknown kind '${f.kind}'`);
      if (f.kind !== 'value' && (f.row === undefined || !(f.row in doc.rows)))
        throw new Error(`snapshot schema: packet '${name}' field '${f.name}' refers to unknown row '${String(f.row)}'`);
    }
  }
}

/**
 * A loaded schema document: validated once, with cached row and packet decoders.
 * Create one per document at module level and take decoders from it.
 *
 * @example
 * import doc from './snapshot-schema.json';
 * const schema = new SnapshotSchema(doc as SnapshotSchemaDocument);
 * const decodePlayer = schema.row<PlayerState>('player');
 * const decodeState = schema.packet<StatePacket>('state'); // rows inside are decoded too
 */
export class SnapshotSchema {
  /** The validated document (read-only; use {@link rowSchema} / {@link packetSchema} for lookups that throw on unknown names). */
  readonly document: SnapshotSchemaDocument;
  private readonly rowDecoders = new Map<string, RowDecoder<unknown>>();
  private readonly packetDecoders = new Map<string, PacketDecoder<unknown>>();

  constructor(document: SnapshotSchemaDocument) {
    validateSnapshotSchema(document);
    this.document = document;
  }

  /** The row schema named `name` (throws when absent). */
  rowSchema(name: string): RowSchema {
    const row = this.document.rows[name];
    if (!row) throw new Error(`snapshot schema: no row '${name}'`);
    return row;
  }

  /** The packet schema named `name` (throws when absent). */
  packetSchema(name: string): PacketSchema {
    const packet = this.document.packets[name];
    if (!packet) throw new Error(`snapshot schema: no packet '${name}'`);
    return packet;
  }

  /** Cached {@link rowDecoder} for the row named `name`. `T` is the client type the row fills (not checked at run time). */
  row<T>(name: string): RowDecoder<T> {
    let d = this.rowDecoders.get(name);
    if (!d) this.rowDecoders.set(name, (d = rowDecoder<unknown>(this.rowSchema(name))));
    return d as RowDecoder<T>;
  }

  /**
   * Cached decoder for the packet named `name`: reads its keys from the packet array (index 0, the
   * message code, is not a field) into an object, decoding `row` / `rows` members with their row
   * decoders and applying `whenMissing` for keys older writers did not send.
   */
  packet<T>(name: string): PacketDecoder<T> {
    let d = this.packetDecoders.get(name);
    if (!d) {
      const fields = this.packetSchema(name).fields;
      const readers: Reader[] = fields.map((f) => {
        const i = f.index;
        if (f.kind === 'row') {
          const dec = this.row<unknown>(f.row!);
          return (p) => dec(p[i] as ArrayLike<unknown>);
        }
        if (f.kind === 'rows') {
          const dec = this.row<unknown>(f.row!);
          return (p) => (p[i] as ArrayLike<unknown>[]).map((r) => dec(r));
        }
        if (f.whenMissing === undefined) return (p) => p[i];
        const fallback = copyDefault(f.whenMissing);
        return (p) => p[i] ?? fallback();
      });
      const names = fields.map((f) => f.name);
      const n = readers.length;
      d = (p) => {
        const out: Record<string, unknown> = {};
        for (let k = 0; k < n; k++) out[names[k]!] = readers[k]!(p);
        return out;
      };
      this.packetDecoders.set(name, d);
    }
    return d as PacketDecoder<T>;
  }
}

/**
 * Names of `schema`'s fields missing from `keys` (e.g. `Object.keys` of an exhaustive
 * `Record<keyof MyType, true>`): a test that a client type covers every wire field.
 * Returns an empty array when all are present.
 */
export function missingFields(schema: RowSchema | PacketSchema, keys: readonly string[]): string[] {
  const have = new Set(keys);
  return schema.fields.map((f) => f.name).filter((n) => !have.has(n));
}
