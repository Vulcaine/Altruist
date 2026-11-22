import { useEffect, useState } from 'preact/hooks';
import { Icon } from '@/ui/basics';
import { useTransport } from './context';
import { formatRoute, useRoute } from './router';
import { GROUPS, type ViewDef } from './views';

const COLLAPSED_KEY = 'altruist.dashboard.navCollapsed';

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1';
  } catch {
    return false;
  }
}

/** The frame: grouped navigation on the left, the routed view on the right. */
export function Shell({ views, features }: { views: readonly ViewDef[]; features: ReadonlySet<string> | null }) {
  const transport = useTransport();
  const available = views.filter((v) => !v.feature || !features || features.has(v.feature));
  const route = useRoute(available[0]?.id ?? 'summary');
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const current = views.find((v) => v.id === route.view) ?? available[0];

  useEffect(() => {
    try {
      localStorage.setItem(COLLAPSED_KEY, collapsed ? '1' : '0');
    } catch {
      // Storage may be unavailable (private window, webview restrictions): the toggle still works for this session.
    }
  }, [collapsed]);

  useEffect(() => {
    document.title = current ? `${current.label} · Altruist` : 'Altruist';
  }, [current]);

  const View = current?.component;
  return (
    <div class={`shell${collapsed ? ' shell-collapsed' : ''}`}>
      <nav class="nav" aria-label="Dashboard">
        <div class="nav-brand">
          <span class="nav-logo">A</span>
          {!collapsed && (
            <div class="nav-brand-text">
              <strong>Altruist</strong>
              <span class="muted" title={transport.serverLabel}>
                {transport.serverLabel}
              </span>
            </div>
          )}
        </div>
        <div class="nav-groups">
          {GROUPS.map((group) => {
            const items = available.filter((v) => v.group === group);
            if (items.length === 0) return null;
            return (
              <div class="nav-group">
                {!collapsed && <div class="nav-group-label">{group}</div>}
                {items.map((v) => (
                  <a href={formatRoute(v.id)} class={v.id === current?.id ? 'nav-item nav-on' : 'nav-item'} title={v.label}>
                    <Icon name={v.icon} />
                    {!collapsed && <span>{v.label}</span>}
                  </a>
                ))}
              </div>
            );
          })}
        </div>
        <button type="button" class="nav-toggle" onClick={() => setCollapsed((c) => !c)} title={collapsed ? 'Expand' : 'Collapse'}>
          <Icon name={collapsed ? 'chevron-right' : 'chevron-left'} />
        </button>
      </nav>
      <main class="main">{View ? <View key={current!.id} rest={route.rest} /> : <div class="loading">No view.</div>}</main>
    </div>
  );
}
