
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1206, hash: 'a1ba38545c22491728ace94069f0f8e148cd4ece7a46e554e90f78755b3c6248', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '60e420bb9bf82d5403a4bb158b5159e564947d24dded3c83398f033dbdc3e883', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-FWPUOQNV.css': {size: 4230, hash: 'h6Ky+5i9ZlQ', text: () => import('./assets-chunks/styles-FWPUOQNV_css.mjs').then(m => m.default)}
  },
};
