import { useEffect, useState } from 'preact/hooks';
import { summaryApi } from '@/api/summary';
import type { Transport } from '@/host/transport';
import { ToastHost } from '@/ui/overlays';
import { TransportContext } from './context';
import { VIEWS } from './registry';
import { Shell } from './Shell';

/** The app: the views the server serves (from its feature list; all of them when it sends none). */
export function App({ transport }: { transport: Transport }) {
  const [features, setFeatures] = useState<ReadonlySet<string> | null>(null);
  useEffect(() => {
    summaryApi(transport)
      .get()
      .then(
        (s) => setFeatures(s.features?.length ? new Set(s.features) : null),
        () => setFeatures(null),
      );
  }, [transport]);
  return (
    <TransportContext.Provider value={transport}>
      <ToastHost>
        <Shell views={VIEWS} features={features} />
      </ToastHost>
    </TransportContext.Provider>
  );
}
