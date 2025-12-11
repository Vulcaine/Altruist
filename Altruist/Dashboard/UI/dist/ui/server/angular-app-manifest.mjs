
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1110, hash: 'acc0f2802aabc7992f5a9f866bad4dd172a88d8728cd4c79c019b67b35deb3c7', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '18d82b94bcf73d191b8cde6f2adcfe45a8f93320918820e86093e0ec10cc0ec5', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-DHT5J3HQ.css': {size: 3914, hash: 'W7o57Ymudx0', text: () => import('./assets-chunks/styles-DHT5J3HQ_css.mjs').then(m => m.default)}
  },
};
