import { createContext } from 'preact';
import { useContext } from 'preact/hooks';
import type { Transport } from '@/host/transport';

/** The transport every view talks to the server through. */
export const TransportContext = createContext<Transport | null>(null);

export function useTransport(): Transport {
  const transport = useContext(TransportContext);
  if (!transport) throw new Error('No dashboard transport: render views inside <App>.');
  return transport;
}
