
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1110, hash: '458dda6588c3a9ba343d27f783caecf7e6a25d0f1b39432c082e4841d84931a6', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'e9d8f2a8983c03132edaa2a3cdcb9f706e19e510886d755c0c622c73fbdae8f0', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-DHT5J3HQ.css': {size: 3914, hash: 'W7o57Ymudx0', text: () => import('./assets-chunks/styles-DHT5J3HQ_css.mjs').then(m => m.default)}
  },
};
