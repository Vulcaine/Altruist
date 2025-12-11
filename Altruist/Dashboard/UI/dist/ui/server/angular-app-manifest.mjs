
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1110, hash: 'ebf3d1dda789e1a219243372916e220e125832b72450c849d5bd0cc91c21c8df', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '9babb5782d998a2ff0d93b10f2262c5d6f5ff4d9ef17cb1a2fa94b9fcf742305', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-7CD2PQ5X.css': {size: 4101, hash: 'uZs2rhgv6ik', text: () => import('./assets-chunks/styles-7CD2PQ5X_css.mjs').then(m => m.default)}
  },
};
