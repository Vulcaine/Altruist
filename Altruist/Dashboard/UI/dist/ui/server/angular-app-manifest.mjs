
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1110, hash: '240a8c12db00a3142b6c8f2c87f5d9483b100ab3fc5a1c6b327b12e87a225616', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'af4962016c1c9d8a4316ece4a5abab558c78b4b94a95e7c032e7d3a0f352290f', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-7CD2PQ5X.css': {size: 4101, hash: 'uZs2rhgv6ik', text: () => import('./assets-chunks/styles-7CD2PQ5X_css.mjs').then(m => m.default)}
  },
};
