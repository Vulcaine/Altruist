
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 942, hash: 'bf84a8c734218d676c81f9785c105cfdea84a141ceef27cf0ac79b59c9b8494e', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'f67608469011fb8455bc27ea645cd049c209fef82d6b636b670f610a1e14462c', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-L3ZXESFG.css': {size: 117, hash: 'ocbHgLtKK/k', text: () => import('./assets-chunks/styles-L3ZXESFG_css.mjs').then(m => m.default)}
  },
};
