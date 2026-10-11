import { render } from 'preact';
import { App } from '@/app/App';
import { createHttpTransport } from '@/host/httpTransport';
import { createVsCodeTransport, type VsCodeApi } from '@/host/vscodeTransport';
import '@/vendor/codicons/codicon.css';
import '@/styles/tokens.css';
import '@/styles/base.css';
import '@/styles/ui.css';

declare global {
  // Defined by VS Code inside a webview only.
  function acquireVsCodeApi(): VsCodeApi;
}

const transport =
  typeof acquireVsCodeApi === 'function'
    ? createVsCodeTransport(acquireVsCodeApi(), (route) => (location.hash = route))
    : createHttpTransport();
document.documentElement.dataset.host = transport.host;
render(<App transport={transport} />, document.getElementById('app')!);
