
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1031, hash: 'c9bc9aa59307c13ae5e533fbed05133786cbf25f0ab498bc67725bc4d097e691', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '4211b4c4d0166f2a9eba7786b959e38c2ce008e5f564dbc414e3777a7f9aa932', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-RJC2EMFM.css': {size: 4103, hash: 'RIaIaZO+uAc', text: () => import('./assets-chunks/styles-RJC2EMFM_css.mjs').then(m => m.default)}
  },
};
