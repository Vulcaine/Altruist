import type { FunctionComponent } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import { ErrorBox, Icon } from '@/ui/basics';
import type { ViewProps } from './views';

/** A view loaded on first use (keeps heavy views, like the 3D world viewer, out of the first download). */
export function lazyView(load: () => Promise<FunctionComponent<ViewProps>>): FunctionComponent<ViewProps> {
  let loaded: FunctionComponent<ViewProps> | null = null;
  return function LazyView(props: ViewProps) {
    const [View, setView] = useState(() => loaded);
    const [error, setError] = useState<Error | null>(null);
    useEffect(() => {
      if (!View)
        load().then(
          (c) => setView(() => (loaded = c)),
          (e: Error) => setError(e),
        );
    }, []);
    if (error) return <ErrorBox error={error} />;
    return View ? <View {...props} /> : <div class="loading"><Icon name="loading" spin /> Loading…</div>;
  };
}
