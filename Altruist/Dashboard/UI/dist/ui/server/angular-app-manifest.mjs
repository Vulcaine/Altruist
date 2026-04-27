
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: false,
  baseHref: '/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 681, hash: '55e720e0ef06b43d600a8095aedf4b9af4078f0b1fe047c377bad8dcc3922fc5', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1221, hash: '07648dcf62c275ef2727755f4e682d309182305c8656e3102c2db906735b35a8', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)}
  },
};
