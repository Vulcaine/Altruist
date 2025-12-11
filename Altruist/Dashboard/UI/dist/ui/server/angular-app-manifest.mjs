
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1188, hash: '8d97c3bb7495db197493d5d839c39dcc761a21a8a439dfdd8c0409d580c78017', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '3695a59de28f44a150a199fac0b7e3271c3be70f39eb76911edd04976aad98dc', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-A6JBY4VQ.css': {size: 4212, hash: 'F45Ls6H/xEk', text: () => import('./assets-chunks/styles-A6JBY4VQ_css.mjs').then(m => m.default)}
  },
};
