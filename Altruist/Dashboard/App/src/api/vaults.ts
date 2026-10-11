import type { Transport } from '@/host/transport';

export interface VaultColumn {
  readonly fieldName: string;
  readonly columnName: string;
  readonly clrType: string;
  readonly isNullable: boolean;
  readonly isPrimaryKey: boolean;
  readonly isIndexed: boolean;
  readonly isUnique: boolean;
  readonly isForeignKey: boolean;
}

export interface VaultDefinition {
  readonly typeKey: string;
  readonly clrType: string;
  readonly clrTypeShort: string;
  readonly keyspace: string;
  readonly tableName: string;
  readonly storeHistory: boolean;
  readonly supportsSqlQuery: boolean;
  readonly columns: readonly VaultColumn[];
}

export type VaultRow = Readonly<Record<string, unknown>>;

export interface VaultPage {
  readonly typeKey: string;
  readonly skip: number;
  readonly take: number;
  readonly total: number;
  readonly fields: readonly string[];
  readonly items: readonly VaultRow[];
}

export interface VaultQueryResult {
  readonly hasRowset: boolean;
  readonly statementKind: string;
  readonly affectedRows?: number | null;
  readonly columns: readonly string[];
  readonly rows: readonly VaultRow[];
}

/** One edited row: its primary-key fields and the changed fields, as the batch update expects. */
export function rowUpdate(definition: VaultDefinition, row: VaultRow, changes: Readonly<Record<string, unknown>>): Record<string, unknown> {
  const keys = definition.columns.filter((c) => c.isPrimaryKey).map((c) => c.fieldName);
  return { ...Object.fromEntries(keys.map((k) => [k, row[k]])), ...changes };
}

/** The text a cell edit stands for, back as the column's value: numbers and booleans parsed, JSON for objects. */
export function parseCell(column: VaultColumn | undefined, text: string): unknown {
  if (text === '' && column?.isNullable) return null;
  const type = (column?.clrType ?? '').toLowerCase();
  if (/int|long|short|byte|single|double|decimal|float/.test(type)) {
    const n = Number(text);
    if (Number.isNaN(n)) throw new Error(`'${text}' is not a number.`);
    return n;
  }
  if (type.includes('bool')) {
    if (!/^(true|false)$/i.test(text)) throw new Error(`'${text}' is not true or false.`);
    return text.toLowerCase() === 'true';
  }
  if (/^[[{]/.test(text.trim())) return JSON.parse(text) as unknown;
  return text;
}

export function cellText(value: unknown): string {
  if (value === null || value === undefined) return '';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

const BASE = '/dashboard/v1/vaults';
const enc = encodeURIComponent;

export const vaultsApi = (t: Transport) => ({
  list: () => t.json<VaultDefinition[]>({ method: 'GET', path: BASE }),
  items: (typeKey: string, skip: number, take: number) => t.json<VaultPage>({ method: 'GET', path: `${BASE}/${enc(typeKey)}/items?skip=${skip}&take=${take}` }),
  update: (typeKey: string, items: readonly Record<string, unknown>[]) =>
    t.json<{ updated: number }>({ method: 'POST', path: `${BASE}/${enc(typeKey)}/batch-update`, body: { typeKey, items } }),
  query: (typeKey: string, sql: string) => t.json<VaultQueryResult>({ method: 'POST', path: `${BASE}/${enc(typeKey)}/query`, body: { sql } }),
});
