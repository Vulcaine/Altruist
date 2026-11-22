import { useCallback, useEffect, useRef, useState } from 'preact/hooks';

/** The state of data loaded from the server. */
export type Resource<T> =
  | { readonly status: 'loading'; readonly data?: T }
  | { readonly status: 'ready'; readonly data: T; readonly at: number }
  | { readonly status: 'error'; readonly error: Error; readonly data?: T };

/**
 * Loads `load()` when `deps` change, again every `pollMs` (when set) and on `reload()`. A slower answer to an older
 * request never overwrites a newer one; the previous data stays visible while reloading.
 */
export function useResource<T>(load: () => Promise<T>, deps: readonly unknown[], pollMs?: number): [Resource<T>, () => void] {
  const [state, setState] = useState<Resource<T>>({ status: 'loading' });
  const generation = useRef(0);
  const loadRef = useRef(load);
  loadRef.current = load;

  const reload = useCallback(() => {
    const mine = ++generation.current;
    setState((s) => ({ status: 'loading', data: s.data }));
    loadRef.current().then(
      (data) => mine === generation.current && setState({ status: 'ready', data, at: Date.now() }),
      (error: Error) => mine === generation.current && setState((s) => ({ status: 'error', error, data: s.data })),
    );
  }, []);

  useEffect(reload, deps);
  useEffect(() => {
    if (!pollMs) return;
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') reload();
    }, pollMs);
    return () => clearInterval(timer);
  }, [pollMs, ...deps]);

  return [state, reload];
}
