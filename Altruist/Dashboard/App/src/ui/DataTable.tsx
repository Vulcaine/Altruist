import type { ComponentChildren } from 'preact';
import { useMemo, useState } from 'preact/hooks';
import { Empty, Icon, SearchInput } from './basics';
import { type Column, nextSort, type Sort, visibleRows } from './table';

export type { Column } from './table';

/** A sortable, filterable table. `render` draws a cell (default: the column's value). */
export function DataTable<T>(props: {
  rows: readonly T[];
  columns: readonly Column<T>[];
  rowKey: (row: T) => string;
  render?: (row: T, column: Column<T>) => ComponentChildren;
  onRowClick?: (row: T) => void;
  selected?: string | null;
  filterable?: boolean;
  toolbar?: ComponentChildren;
  empty?: ComponentChildren;
  initialSort?: Sort;
  maxRows?: number;
}) {
  const { rows, columns, rowKey, render, onRowClick, selected, filterable = true, toolbar, empty, initialSort, maxRows = 2000 } = props;
  const [filter, setFilter] = useState('');
  const [sort, setSort] = useState<Sort | null>(initialSort ?? null);
  const shown = useMemo(() => visibleRows(rows, columns, filter, sort), [rows, columns, filter, sort]);
  const clipped = shown.length > maxRows;

  return (
    <div class="table-wrap">
      {(filterable || toolbar) && (
        <div class="table-tools">
          {filterable && <SearchInput value={filter} onInput={setFilter} />}
          <span class="muted">{filter ? `${shown.length} of ${rows.length}` : `${rows.length} rows`}</span>
          <div class="grow" />
          {toolbar}
        </div>
      )}
      {shown.length === 0 ? (
        <Empty>{rows.length === 0 ? (empty ?? 'Nothing here yet.') : 'No row matches the filter.'}</Empty>
      ) : (
        <div class="table-scroll">
          <table class="table">
            <thead>
              <tr>
                {columns.map((c) => (
                  <th
                    style={c.width ? { width: c.width } : undefined}
                    class={`${c.align === 'right' ? 'num' : ''}${c.sortable === false ? '' : ' sortable'}`}
                    onClick={c.sortable === false ? undefined : () => setSort(nextSort(sort, c.id))}
                  >
                    {c.label}
                    {sort?.column === c.id && <Icon name={sort.descending ? 'chevron-down' : 'chevron-up'} />}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {shown.slice(0, maxRows).map((row) => {
                const key = rowKey(row);
                return (
                  <tr key={key} class={`${onRowClick ? 'clickable' : ''}${key === selected ? ' selected' : ''}`} onClick={onRowClick && (() => onRowClick(row))}>
                    {columns.map((c) => (
                      <td class={c.align === 'right' ? 'num' : undefined}>{render ? render(row, c) : String(c.value(row) ?? '')}</td>
                    ))}
                  </tr>
                );
              })}
            </tbody>
          </table>
          {clipped && <div class="muted table-clip">Showing the first {maxRows} of {shown.length}; narrow the filter to see the rest.</div>}
        </div>
      )}
    </div>
  );
}
