
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1188, hash: '085e2ead4ffeb576741dd9e1cd8cd2c54a52e96665bda27ab5c0552ff09fe258', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: 'f1674bda0b61dcf94f5878de5a9a838f885f98937957b4d0e9aa6c726af79bd9', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-A6JBY4VQ.css': {size: 4212, hash: 'F45Ls6H/xEk', text: () => import('./assets-chunks/styles-A6JBY4VQ_css.mjs').then(m => m.default)}
  },
};
