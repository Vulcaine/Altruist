
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1188, hash: '8ef4a1162e440f9fb86b79513f0f97ca4e29ef81e66a3c1118f35c9165f62873', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'fe980ad38048a5b829e8d5d68510476177ee4335a449652b6825d8019158f265', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-A6JBY4VQ.css': {size: 4212, hash: 'F45Ls6H/xEk', text: () => import('./assets-chunks/styles-A6JBY4VQ_css.mjs').then(m => m.default)}
  },
};
