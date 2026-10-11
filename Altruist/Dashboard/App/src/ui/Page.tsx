import type { ComponentChildren } from 'preact';
import type { Resource } from '@/app/resource';
import { formatAgo } from './format';
import { Button, Icon } from './basics';

/** A view's frame: title, what it shows, freshness, refresh, and the view's own actions. */
export function Page({
  title,
  subtitle,
  actions,
  resource,
  reload,
  children,
  fill,
}: {
  title: ComponentChildren;
  subtitle?: ComponentChildren;
  actions?: ComponentChildren;
  resource?: Resource<unknown>;
  reload?: () => void;
  children: ComponentChildren;
  /** The body fills the height (for canvases and split panes) instead of scrolling. */
  fill?: boolean;
}) {
  return (
    <div class={`page${fill ? ' page-fill' : ''}`}>
      <header class="page-head">
        <div class="grow">
          <h1>{title}</h1>
          {subtitle && <div class="muted">{subtitle}</div>}
        </div>
        <div class="row">
          {actions}
          {resource && (
            <span class="muted fresh" title="Last loaded">
              {resource.status === 'loading' ? <Icon name="loading" spin /> : resource.status === 'ready' ? formatAgo(resource.at) : null}
            </span>
          )}
          {reload && <Button icon="refresh" title="Refresh" onClick={reload} />}
        </div>
      </header>
      <div class="page-body">{children}</div>
    </div>
  );
}
