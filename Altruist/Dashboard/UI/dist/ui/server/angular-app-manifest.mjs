
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: false,
  baseHref: '/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 681, hash: '1eae881180f861bebaf61ca807d56f64e8e484e2b6a951d229756c7893f1b110', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1221, hash: 'fe6bb6404ffb0a0c9aa4b2edf8f888da11b249bb144d67e80c6b4a019b24334e', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)}
  },
};
