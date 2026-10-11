import type { ComponentChildren } from 'preact';
import type { Resource } from '@/app/resource';
import { ErrorBox, Icon } from './basics';

/** Draws loaded data; a spinner before the first answer; an error (with retry) when loading failed. */
export function ResourceView<T>({ resource, reload, children }: { resource: Resource<T>; reload: () => void; children: (data: T) => ComponentChildren }) {
  return (
    <>
      {resource.status === 'error' && <ErrorBox error={resource.error} onRetry={reload} />}
      {resource.data !== undefined ? (
        children(resource.data)
      ) : resource.status === 'loading' ? (
        <div class="loading">
          <Icon name="loading" spin /> Loading…
        </div>
      ) : null}
    </>
  );
}

/** A JSON value, pretty-printed. */
export function JsonView({ value, max = 20000 }: { value: unknown; max?: number }) {
  const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
  return <pre class="json">{text && text.length > max ? text.slice(0, max) + `\n… ${text.length - max} more characters` : text}</pre>;
}

/** A small line chart of recent values. */
export function Sparkline({ values, width = 120, height = 28 }: { values: readonly number[]; width?: number; height?: number }) {
  if (values.length < 2) return <svg width={width} height={height} />;
  const max = Math.max(...values);
  const min = Math.min(...values);
  const span = max - min || 1;
  const points = values
    .map((v, i) => `${((i / (values.length - 1)) * width).toFixed(1)},${(height - 2 - ((v - min) / span) * (height - 4)).toFixed(1)}`)
    .join(' ');
  return (
    <svg class="spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
      <polyline points={points} fill="none" stroke="currentColor" stroke-width="1.5" />
    </svg>
  );
}
