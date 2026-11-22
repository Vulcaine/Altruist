/** A table column: how to read, sort and filter one field of a row. */
export interface Column<T> {
  readonly id: string;
  readonly label: string;
  /** The value sorted and filtered on. */
  readonly value: (row: T) => string | number | boolean | null | undefined;
  readonly align?: 'right';
  readonly width?: string;
  /** Sorting on this column; default true. */
  readonly sortable?: boolean;
}

export interface Sort {
  readonly column: string;
  readonly descending: boolean;
}

function compare(a: unknown, b: unknown): number {
  if (a === b) return 0;
  if (a === null || a === undefined || a === '') return 1;
  if (b === null || b === undefined || b === '') return -1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

/** The rows that match `filter` (every word in some column), sorted; never changes `rows`. */
export function visibleRows<T>(rows: readonly T[], columns: readonly Column<T>[], filter: string, sort: Sort | null): T[] {
  const words = filter.toLowerCase().split(/\s+/).filter(Boolean);
  const matching = words.length
    ? rows.filter((row) => {
        const text = columns.map((c) => String(c.value(row) ?? '')).join(' ').toLowerCase();
        return words.every((w) => text.includes(w));
      })
    : [...rows];
  const column = sort && columns.find((c) => c.id === sort.column);
  if (!column) return matching;
  const sign = sort!.descending ? -1 : 1;
  return matching
    .map((row, index) => ({ row, index }))
    .sort((a, b) => sign * compare(column.value(a.row), column.value(b.row)) || a.index - b.index)
    .map((x) => x.row);
}

/** Clicking a header: ascending, then descending, then unsorted. */
export function nextSort(current: Sort | null, column: string): Sort | null {
  if (!current || current.column !== column) return { column, descending: false };
  return current.descending ? null : { column, descending: true };
}
