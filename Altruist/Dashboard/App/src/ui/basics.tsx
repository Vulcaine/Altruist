import type { ComponentChildren, JSX } from 'preact';

type Tone = 'ok' | 'warn' | 'danger' | 'muted' | 'accent';

export function Button(props: JSX.ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'ghost' | 'danger'; small?: boolean; icon?: string }) {
  const { variant = 'ghost', small, icon, children, class: cls, ...rest } = props;
  return (
    <button type="button" {...rest} class={`btn btn-${variant}${small ? ' btn-small' : ''}${cls ? ' ' + cls : ''}`}>
      {icon && <Icon name={icon} />}
      {children}
    </button>
  );
}

/** A codicon (VS Code's icon font, bundled for the browser too). */
export function Icon({ name, spin }: { name: string; spin?: boolean }) {
  return <i class={`codicon codicon-${name}${spin ? ' codicon-modifier-spin' : ''}`} aria-hidden="true" />;
}

export function Badge({ tone = 'muted', children, title }: { tone?: Tone; children: ComponentChildren; title?: string }) {
  return (
    <span class={`badge badge-${tone}`} title={title}>
      {children}
    </span>
  );
}

export function Card({ title, actions, children, flush, class: cls }: { title?: ComponentChildren; actions?: ComponentChildren; children: ComponentChildren; flush?: boolean; class?: string }) {
  return (
    <section class={`card${flush ? ' card-flush' : ''}${cls ? ' ' + cls : ''}`}>
      {(title || actions) && (
        <header class="card-head">
          <h2>{title}</h2>
          <div class="row">{actions}</div>
        </header>
      )}
      <div class="card-body">{children}</div>
    </section>
  );
}

export function Stat({ label, value, hint, tone }: { label: string; value: ComponentChildren; hint?: ComponentChildren; tone?: Tone }) {
  return (
    <div class={`stat${tone ? ' stat-' + tone : ''}`}>
      <div class="stat-label">{label}</div>
      <div class="stat-value">{value}</div>
      {hint !== undefined && <div class="stat-hint">{hint}</div>}
    </div>
  );
}

export function Stats({ children }: { children: ComponentChildren }) {
  return <div class="stats">{children}</div>;
}

export function KeyValues({ items }: { items: readonly (readonly [string, ComponentChildren])[] }) {
  return (
    <dl class="kv">
      {items.map(([k, v]) => (
        <>
          <dt>{k}</dt>
          <dd>{v ?? <span class="muted">—</span>}</dd>
        </>
      ))}
    </dl>
  );
}

export function SearchInput({ value, onInput, placeholder = 'Filter…' }: { value: string; onInput: (value: string) => void; placeholder?: string }) {
  return (
    <label class="search">
      <Icon name="search" />
      <input value={value} placeholder={placeholder} onInput={(e) => onInput(e.currentTarget.value)} />
    </label>
  );
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: readonly (readonly [T, string])[]; value: T; onChange: (value: T) => void }) {
  return (
    <div class="tabs" role="tablist">
      {tabs.map(([id, label]) => (
        <button type="button" role="tab" aria-selected={id === value} class={id === value ? 'tab tab-on' : 'tab'} onClick={() => onChange(id)}>
          {label}
        </button>
      ))}
    </div>
  );
}

export function Empty({ icon = 'inbox', children }: { icon?: string; children: ComponentChildren }) {
  return (
    <div class="empty">
      <Icon name={icon} />
      <div>{children}</div>
    </div>
  );
}

export function ErrorBox({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  return (
    <div class="error-box" role="alert">
      <Icon name="error" />
      <div class="grow">{error.message}</div>
      {onRetry && (
        <Button small icon="refresh" onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  );
}
