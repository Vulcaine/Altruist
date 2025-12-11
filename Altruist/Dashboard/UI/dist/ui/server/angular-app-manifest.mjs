
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1110, hash: '851d83dd01aaa7ee26a6a1b7fb58b9b5a0a51db9896d3ad3bc07016b4e6aaf08', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'e1f543c62822e4aae878f3132c068886eaec46b978091c78a78a8c6b31e3dfdd', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-DHT5J3HQ.css': {size: 3914, hash: 'W7o57Ymudx0', text: () => import('./assets-chunks/styles-DHT5J3HQ_css.mjs').then(m => m.default)}
  },
};
