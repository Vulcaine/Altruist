import { useEffect, useState } from 'preact/hooks';

/** A dashboard location: the view id and its optional sub-path (e.g. "vaults/players"). */
export interface Route {
  readonly view: string;
  readonly rest: readonly string[];
}

/** Parses "#/vaults/players" into { view: "vaults", rest: ["players"] }. */
export function parseRoute(hash: string, fallback: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent);
  return { view: parts[0] ?? fallback, rest: parts.slice(1) };
}

export function formatRoute(view: string, ...rest: string[]): string {
  return '#/' + [view, ...rest].map(encodeURIComponent).join('/');
}

export function navigate(view: string, ...rest: string[]): void {
  location.hash = formatRoute(view, ...rest);
}

/** The current route, following hash changes. */
export function useRoute(fallback: string): Route {
  const [route, setRoute] = useState(() => parseRoute(location.hash, fallback));
  useEffect(() => {
    const onChange = () => setRoute(parseRoute(location.hash, fallback));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, [fallback]);
  return route;
}
